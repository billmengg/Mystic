using System;
using System.Collections.Concurrent;
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
    /// <summary>
    /// Face tracking from the VTube Studio iPhone app, in its "3rd Party PC Clients" mode (the same link VSeeFace
    /// uses). Mystic asks the phone (port 21412) to stream to a local port, repeating the request every second; the
    /// phone answers ~60 times a second with ARKit blendshapes, head rotation/position and hotkey presses.
    /// Frames go into TrackerReceiver as an external source, so the whole pipeline (Perfect Sync, moods, motion gate,
    /// calibration) works unchanged, and the webcam tracker can still provide arms/hands.
    /// Axis directions were not verifiable from a recording, hence the flip switches.
    /// </summary>
    public class VtsPhoneSource : MonoBehaviour
    {
        public TrackerReceiver receiver;
        public AvatarDriver driver;

        public bool phoneEnabled;
        public string phoneIp = "";
        [Tooltip("Local port the phone sends tracking to. Fixed (not random) so a firewall rule can allow it.")]
        public int replyPort = 39572;

        [Header("Directions")]
        public bool flipTurn;
        public bool flipNod;
        public bool flipTilt;
        public bool flipLeanSideways;
        public bool flipLeanForward;
        [Tooltip("Phone hotkey buttons 1-5 toggle the moods, 6 clears the mood.")]
        public bool phoneHotkeysSetMood = true;

        const int PhonePort = 21412;

        UdpClient _udp;
        Thread _thread;
        volatile bool _running;
        string _activeIp;
        IPEndPoint _target;
        int _localPort;
        int _seq = 1000000; // well apart from the webcam tracker's sequence numbers
        int _lastHotkey = -1;
        readonly ConcurrentQueue<int> _hotkeys = new ConcurrentQueue<int>();

        long _lastPacketTicks;
        int _packetsThisSecond;
        long _secondStart;
        volatile int _rate;

        public string Error { get; private set; }
        public int PacketsPerSecond => Connected ? _rate : 0;

        public bool Connected
        {
            get
            {
                long t = Interlocked.Read(ref _lastPacketTicks);
                return t != 0 && (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency < 1.0;
            }
        }

        void Update()
        {
            string ip = (phoneIp ?? "").Trim();
            if (phoneEnabled && (_thread == null || ip != _activeIp)) StartLink(ip);
            else if (!phoneEnabled && _thread != null) StopLink();

            while (_hotkeys.TryDequeue(out int key))
            {
                if (!phoneHotkeysSetMood || driver == null) continue;
                if (key >= 1 && key <= 5) driver.ToggleMood((AvatarDriver.Mood)key);
                else if (key == 6) driver.ToggleMood(driver.CurrentMood); // toggling the current mood clears it
            }
        }

        void OnDisable() => StopLink();

        void StartLink(string ip)
        {
            StopLink();
            Error = null;
            if (!IPAddress.TryParse(ip, out var address))
            {
                Error = "Enter the IP address VTube Studio shows on the phone";
                _activeIp = ip;
                return;
            }
            try
            {
                // The phone replies to this port. Windows Firewall must allow it for Unity (or Mystic.exe).
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, replyPort));
                _udp.Client.ReceiveTimeout = 250;
                _localPort = replyPort;
            }
            catch (SocketException e)
            {
                Error = $"Port {replyPort} is in use ({e.Message})";
                phoneEnabled = false;
                return;
            }
            _target = new IPEndPoint(address, PhonePort);
            _activeIp = ip;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MysticVtsPhone" };
            _thread.Start();
            Debug.Log($"[Mystic] Requesting tracking from VTube Studio at {ip}:{PhonePort}, replies to port {_localPort}");
        }

        void StopLink()
        {
            _running = false;
            _udp?.Close();
            _thread?.Join(500);
            _udp = null;
            _thread = null;
            _activeIp = null;
        }

        void Loop()
        {
            var request = Encoding.UTF8.GetBytes(
                $"{{\"messageType\":\"iOSTrackingDataRequest\",\"time\":3,\"sentBy\":\"Mystic\",\"ports\":[{_localPort}]}}");
            var from = new IPEndPoint(IPAddress.Any, 0);
            long nextRequest = 0;
            _secondStart = Stopwatch.GetTimestamp();

            while (_running)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextRequest)
                {
                    // The phone streams for "time" seconds per request; asking every second keeps it going.
                    try { _udp.Send(request, request.Length, _target); }
                    catch (Exception) { if (!_running) return; }
                    nextRequest = now + Stopwatch.Frequency;
                }

                byte[] data;
                try { data = _udp.Receive(ref from); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { continue; }
                catch (SocketException) { if (!_running) return; continue; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    var frame = Parse(data);
                    receiver?.PublishExternal(frame);
                    Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
                    CountRate();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Mystic] Bad VTube Studio packet: {e.Message}");
                }
            }
        }

        void CountRate()
        {
            _packetsThisSecond++;
            long now = Stopwatch.GetTimestamp();
            if (now - _secondStart >= Stopwatch.Frequency)
            {
                _rate = _packetsThisSecond;
                _packetsThisSecond = 0;
                _secondStart = now;
            }
        }

        TrackerFrame Parse(byte[] data)
        {
            var j = JObject.Parse(Encoding.UTF8.GetString(data));
            var f = new TrackerFrame
            {
                Seq = ++_seq,
                Time = ((double?)j["Timestamp"] ?? 0.0) / 1000.0,
                Valid = (bool?)j["FaceFound"] ?? false,
            };

            int hotkey = (int?)j["Hotkey"] ?? -1;
            if (hotkey != _lastHotkey && hotkey >= 1) _hotkeys.Enqueue(hotkey);
            _lastHotkey = hotkey;

            if (!f.Valid) return f;

            // Rotation is in degrees: x = turn, y = nod, z = tilt (confirmed with a live test; signs via the flips).
            var r = j["Rotation"];
            float turn = (float)r["x"] * (flipTurn ? -1f : 1f);
            float nod = (float)r["y"] * (flipNod ? -1f : 1f);
            float tilt = (float)r["z"] * (flipTilt ? -1f : 1f);
            f.HeadRot = Quaternion.Euler(nod, turn, tilt);

            // Position is in centimetres.
            var p = j["Position"];
            f.HeadPos = new Vector3(
                (float)p["x"] * (flipLeanSideways ? -1f : 1f),
                (float)p["y"],
                (float)p["z"] * (flipLeanForward ? -1f : 1f)) * 0.01f;

            // ARKit names with a capital first letter ("EyeBlinkRight") -> the tracker's names ("eyeBlinkRight").
            if (j["BlendShapes"] is JArray shapes)
            {
                foreach (var s in shapes)
                {
                    var k = (string)s["k"];
                    if (string.IsNullOrEmpty(k)) continue;
                    f.Blendshapes[char.ToLowerInvariant(k[0]) + k.Substring(1)] = (float)s["v"];
                }
            }

            if (receiver != null && receiver.mirror) Mirror(f);
            return f;
        }

        // Same mirror convention as the webcam tracker (TrackerReceiver.mirror).
        static void Mirror(TrackerFrame f)
        {
            f.HeadPos.x = -f.HeadPos.x;
            f.HeadRot = new Quaternion(f.HeadRot.x, -f.HeadRot.y, -f.HeadRot.z, f.HeadRot.w);
            var copy = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>>(f.Blendshapes);
            f.Blendshapes.Clear();
            foreach (var kv in copy)
            {
                string k = kv.Key.EndsWith("Left") ? kv.Key.Substring(0, kv.Key.Length - 4) + "Right"
                    : kv.Key.EndsWith("Right") ? kv.Key.Substring(0, kv.Key.Length - 5) + "Left" : kv.Key;
                f.Blendshapes[k] = kv.Value;
            }
        }
    }
}
