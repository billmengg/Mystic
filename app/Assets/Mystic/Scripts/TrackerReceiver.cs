using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mystic
{
    /// <summary>Receives protocol-v1 UDP packets on a background thread (see protocol/PROTOCOL.md).</summary>
    public class TrackerReceiver : MonoBehaviour
    {
        public int port = 39570; // not 39539: that is the VMC protocol default, used by VmcProtocol
        [Tooltip("Mirror movement: the avatar moves like your reflection (your right hand raises the hand on the " +
                 "same side of the screen) instead of like a puppet facing you.")]
        public bool mirror;

        readonly object _lock = new object();
        TrackerFrame _latest;          // from the webcam tracker
        TrackerFrame _latestExternal;  // from another face source, e.g. the iPhone (VtsPhoneSource)
        long _lastPacketTicks, _lastWebcamTicks, _lastExternalTicks;
        UdpClient _udp;
        Thread _thread;

        /// <summary>The newest frame. An external face source (iPhone) wins while it is sending; the webcam
        /// tracker's arms/hands are merged into its frames, so body tracking keeps working alongside it.</summary>
        public TrackerFrame Latest
        {
            get
            {
                lock (_lock) return ExternalActive ? _latestExternal : _latest;
            }
        }

        public bool ExternalActive => _latestExternal != null && Age(_lastExternalTicks) < 0.5;

        static double Age(long ticks)
        {
            long t = Interlocked.Read(ref ticks);
            if (t == 0) return double.PositiveInfinity;
            return (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
        }

        /// <summary>Hands in a face frame from another source (called from that source's thread).</summary>
        public void PublishExternal(TrackerFrame frame)
        {
            lock (_lock)
            {
                // Borrow the webcam tracker's body data if it is current, so --pose/--hands still drive the arms.
                if (_latest != null && Age(_lastWebcamTicks) < 0.3)
                {
                    frame.Pose = _latest.Pose;
                    frame.PoseVisibility = _latest.PoseVisibility;
                    frame.HandLeft = _latest.HandLeft;
                    frame.HandRight = _latest.HandRight;
                }
                _latestExternal = frame;
            }
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastExternalTicks, now);
            Interlocked.Exchange(ref _lastPacketTicks, now);
        }

        /// <summary>Seconds since the last packet arrived, or +infinity if none yet.</summary>
        public double SecondsSincePacket
        {
            get
            {
                long t = Interlocked.Read(ref _lastPacketTicks);
                if (t == 0) return double.PositiveInfinity;
                return (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
            }
        }

        void OnEnable()
        {
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException e)
            {
                Debug.LogError($"[Mystic] Cannot bind UDP port {port}: {e.Message}");
                return;
            }
            _thread = new Thread(Run) { IsBackground = true, Name = "MysticTrackerReceiver" };
            _thread.Start();
        }

        void OnDisable()
        {
            _udp?.Close();
            _thread?.Join(500);
            _udp = null;
            _thread = null;
        }

        void Run()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] buf;
                try { buf = _udp.Receive(ref ep); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    var frame = Parse(buf, mirror);
                    lock (_lock) _latest = frame;
                    long now = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _lastWebcamTicks, now);
                    Interlocked.Exchange(ref _lastPacketTicks, now);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Mystic] Bad tracker packet: {e.Message}");
                }
            }
        }

        // Protocol space: right-handed, +Y up, camera looks down -Z, cm. Face-forward is +Z (toward the camera)
        // and the subject's left is +X. Unity avatars face +Z with their left on -X, so mirror X:
        // position (x,y,z) -> (-x,y,z); rotation quaternion (x,y,z,w) -> (x,-y,-z,w).
        static TrackerFrame Parse(byte[] buf, bool mirror)
        {
            var j = JObject.Parse(Encoding.UTF8.GetString(buf));
            var f = new TrackerFrame
            {
                Seq = (int)j["seq"],
                Time = (double)j["t"],
                Valid = (bool)j["valid"],
            };

            // Body data can arrive even when the face isn't found, so read it first.
            if (j["pose"]?["world"] is JArray pose)
            {
                f.Pose = new Vector3[pose.Count];
                f.PoseVisibility = new float[pose.Count];
                for (int i = 0; i < pose.Count; i++)
                {
                    var p3 = (JArray)pose[i];
                    f.Pose[i] = ToUnity(p3);
                    f.PoseVisibility[i] = p3.Count > 3 ? (float)p3[3] : 1f;
                }
            }
            if (j["hands"] is JObject hands)
            {
                f.HandLeft = ReadHand(hands["left"]);
                f.HandRight = ReadHand(hands["right"]);
            }
            if (mirror) MirrorBody(f);

            if (!f.Valid) return f;

            var p = (JArray)j["head"]["pos"];
            var q = (JArray)j["head"]["quat"];
            f.HeadPos = new Vector3(-(float)p[0], (float)p[1], (float)p[2]) * 0.01f;
            f.HeadRot = new Quaternion((float)q[0], -(float)q[1], -(float)q[2], (float)q[3]).normalized;
            if (mirror)
            {
                // Reflect left/right once more: x flips, and a rotation (x,y,z,w) reflected across x becomes (x,-y,-z,w).
                f.HeadPos.x = -f.HeadPos.x;
                f.HeadRot = new Quaternion(f.HeadRot.x, -f.HeadRot.y, -f.HeadRot.z, f.HeadRot.w);
            }
            foreach (var kv in (JObject)j["bs"])
                f.Blendshapes[mirror ? SwapSide(kv.Key) : kv.Key] = (float)kv.Value;
            return f;
        }

        // eyeBlinkLeft <-> eyeBlinkRight, jawLeft <-> jawRight, ... (ARKit names end in Left/Right).
        static string SwapSide(string name)
        {
            if (name.EndsWith("Left")) return name.Substring(0, name.Length - 4) + "Right";
            if (name.EndsWith("Right")) return name.Substring(0, name.Length - 5) + "Left";
            return name;
        }

        // MediaPipe pose pairs (left, right): eyes/ears/mouth, shoulders, elbows, wrists, hand points, hips, legs, feet.
        static readonly int[,] PosePairs =
        {
            { 1, 4 }, { 2, 5 }, { 3, 6 }, { 7, 8 }, { 9, 10 }, { 11, 12 }, { 13, 14 }, { 15, 16 }, { 17, 18 },
            { 19, 20 }, { 21, 22 }, { 23, 24 }, { 25, 26 }, { 27, 28 }, { 29, 30 }, { 31, 32 },
        };

        static void MirrorBody(TrackerFrame f)
        {
            if (f.Pose != null)
            {
                for (int i = 0; i < f.Pose.Length; i++) f.Pose[i].x = -f.Pose[i].x;
                for (int k = 0; k < PosePairs.GetLength(0); k++)
                {
                    int a = PosePairs[k, 0], b = PosePairs[k, 1];
                    if (b >= f.Pose.Length) continue;
                    (f.Pose[a], f.Pose[b]) = (f.Pose[b], f.Pose[a]);
                    if (f.PoseVisibility != null)
                        (f.PoseVisibility[a], f.PoseVisibility[b]) = (f.PoseVisibility[b], f.PoseVisibility[a]);
                }
            }
            if (f.HandLeft != null) for (int i = 0; i < f.HandLeft.Length; i++) f.HandLeft[i].x = -f.HandLeft[i].x;
            if (f.HandRight != null) for (int i = 0; i < f.HandRight.Length; i++) f.HandRight[i].x = -f.HandRight[i].x;
            (f.HandLeft, f.HandRight) = (f.HandRight, f.HandLeft);
        }

        // Body/hand points: x right, y down, z away from the camera -> Unity avatar axes (-x, -y, -z). Same mirror
        // convention as the head: the user's left side drives the avatar's left side.
        static Vector3 ToUnity(JArray p) => new Vector3(-(float)p[0], -(float)p[1], -(float)p[2]);

        static Vector3[] ReadHand(JToken hand)
        {
            if (!(hand?["world"] is JArray pts) || pts.Count < 21) return null;
            var result = new Vector3[21];
            for (int i = 0; i < 21; i++) result[i] = ToUnity((JArray)pts[i]);
            return result;
        }
    }
}
