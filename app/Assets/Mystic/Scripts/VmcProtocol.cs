using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UniVRM10;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mystic
{
    /// <summary>
    /// VMC protocol (OSC over UDP), both directions.
    /// Receive: bone rotations and blendshapes from apps like Waidayo / iFacialMocap (iPhone ARKit), applied on top of
    /// the webcam tracking for whatever the app sends. Send: this avatar's bones and expressions, for VNyan, Warudo, etc.
    /// VMC bones are local rotations of a "normalized" humanoid (every bone identity in the T-pose), so each model is
    /// converted through its own T-pose (AvatarDriver.TryGetTPose).
    /// Runs after AvatarDriver and ArmDriver, so received data wins; UniVRM applies it in its LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class VmcProtocol : MonoBehaviour
    {
        public AvatarLoader loader;
        public AvatarDriver driver;

        [Header("Receive")]
        public bool receiveEnabled;
        public int receivePort = 39539; // VMC default
        [Tooltip("Off: only head, neck, spine and eyes are taken (face apps often send their arms stuck in a T-pose). " +
                 "On: every bone, for full-body tracking apps.")]
        public bool receiveAllBones;

        static readonly HashSet<HumanBodyBones> UpperBones = new HashSet<HumanBodyBones>
        {
            HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck,
            HumanBodyBones.Head, HumanBodyBones.LeftEye, HumanBodyBones.RightEye, HumanBodyBones.Jaw,
        };

        [Header("Send")]
        public bool sendEnabled;
        public string sendHost = "127.0.0.1";
        public int sendPort = 39540;    // not 39539, so Mystic doesn't send to itself when it also receives

        // ---- shared with the network thread
        readonly object _lock = new object();
        readonly Dictionary<string, Quaternion> _bonesIn = new Dictionary<string, Quaternion>();
        readonly Dictionary<string, float> _blendPending = new Dictionary<string, float>();
        readonly Dictionary<string, float> _blendApplied = new Dictionary<string, float>();
        long _lastPacketTicks;
        string _lastSender;

        // ---- main thread
        UdpClient _rx;
        Thread _rxThread;
        int _rxPort = -1;
        UdpClient _tx;
        IPEndPoint _txTarget;
        string _txHost;
        int _txPort = -1;
        readonly Osc.BundleWriter _writer = new Osc.BundleWriter();
        readonly Dictionary<HumanBodyBones, Quaternion> _bones = new Dictionary<HumanBodyBones, Quaternion>();
        readonly Dictionary<string, float> _blends = new Dictionary<string, float>();
        readonly Dictionary<HumanBodyBones, Quaternion> _world = new Dictionary<HumanBodyBones, Quaternion>();
        readonly Dictionary<string, ExpressionKey> _exprByName = new Dictionary<string, ExpressionKey>();
        Vrm10Instance _vrm;

        public string ReceiveError { get; private set; }
        public string SendError { get; private set; }
        public int BonesReceived => _bones.Count;
        public int BlendsReceived => _blends.Count;
        public string LastSender => _lastSender;

        public bool Receiving
        {
            get
            {
                long t = Interlocked.Read(ref _lastPacketTicks);
                return receiveEnabled && t != 0 && (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency < 1.0;
            }
        }

        // VRM 0.x preset names (what most VMC apps use) <-> VRM 1.0 presets.
        static readonly Dictionary<string, string> Vrm0ToVrm1 = new Dictionary<string, string>
        {
            ["a"] = "aa", ["i"] = "ih", ["u"] = "ou", ["e"] = "ee", ["o"] = "oh",
            ["joy"] = "happy", ["sorrow"] = "sad", ["fun"] = "relaxed",
            ["blink_l"] = "blinkleft", ["blink_r"] = "blinkright",
        };

        static readonly Dictionary<ExpressionPreset, string> Vrm1ToVrm0 = new Dictionary<ExpressionPreset, string>
        {
            [ExpressionPreset.aa] = "A", [ExpressionPreset.ih] = "I", [ExpressionPreset.ou] = "U",
            [ExpressionPreset.ee] = "E", [ExpressionPreset.oh] = "O", [ExpressionPreset.blink] = "Blink",
            [ExpressionPreset.blinkLeft] = "Blink_L", [ExpressionPreset.blinkRight] = "Blink_R",
            [ExpressionPreset.happy] = "Joy", [ExpressionPreset.angry] = "Angry", [ExpressionPreset.sad] = "Sorrow",
            [ExpressionPreset.relaxed] = "Fun", [ExpressionPreset.surprised] = "Surprised",
            [ExpressionPreset.lookUp] = "LookUp", [ExpressionPreset.lookDown] = "LookDown",
            [ExpressionPreset.lookLeft] = "LookLeft", [ExpressionPreset.lookRight] = "LookRight",
            [ExpressionPreset.neutral] = "Neutral",
        };

        void Start()
        {
            if (loader == null) return;
            loader.Loaded += OnLoaded;
            if (loader.Avatar != null) OnLoaded(loader.Avatar);
        }

        void OnDestroy()
        {
            if (loader != null) loader.Loaded -= OnLoaded;
            StopReceiver();
            StopSender();
        }

        void OnLoaded(Vrm10Instance vrm)
        {
            _vrm = vrm;
            _exprByName.Clear();
            foreach (var key in vrm.Runtime.Expression.ExpressionKeys)
            {
                string name = key.Preset == ExpressionPreset.custom ? key.Name : key.Preset.ToString();
                _exprByName[name.ToLowerInvariant()] = key;
            }
        }

        // ------------------------------------------------------------------ receive

        void Update()
        {
            if (receiveEnabled && (_rx == null || _rxPort != receivePort)) StartReceiver();
            else if (!receiveEnabled && _rx != null) StopReceiver();

            if (_vrm == null || driver == null || !Receiving || driver.trackingPaused) return;
            Snapshot();
            ApplyBones();
            ApplyBlends();
        }

        void StartReceiver()
        {
            StopReceiver();
            ReceiveError = null;
            try
            {
                _rx = new UdpClient(new IPEndPoint(IPAddress.Any, receivePort)); // Any: phones send over Wi-Fi
                _rxPort = receivePort;
            }
            catch (SocketException e)
            {
                ReceiveError = $"Port {receivePort} is in use ({e.Message})";
                receiveEnabled = false;
                _rx = null;
                return;
            }
            _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "MysticVmcReceive" };
            _rxThread.Start();
            Debug.Log($"[Mystic] VMC receiving on port {receivePort}");
        }

        void StopReceiver()
        {
            _rx?.Close();
            _rxThread?.Join(500);
            _rx = null;
            _rxThread = null;
            _rxPort = -1;
        }

        void ReceiveLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            var messages = new List<Osc.Message>(256);
            var client = _rx;
            while (true)
            {
                byte[] buf;
                try { buf = client.Receive(ref ep); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }

                messages.Clear();
                try { Osc.Decode(buf, 0, buf.Length, messages); }
                catch (Exception) { continue; }

                lock (_lock)
                {
                    foreach (var m in messages)
                    {
                        switch (m.Address)
                        {
                            case "/VMC/Ext/Bone/Pos":
                                var name = m.S(0);
                                if (name != null) _bonesIn[name] = new Quaternion(m.F(4), m.F(5), m.F(6), m.F(7));
                                break;
                            case "/VMC/Ext/Blend/Val":
                                var blend = m.S(0);
                                if (blend != null) _blendPending[blend] = m.F(1);
                                break;
                            case "/VMC/Ext/Blend/Apply":
                                foreach (var kv in _blendPending) _blendApplied[kv.Key] = kv.Value;
                                _blendPending.Clear();
                                break;
                        }
                    }
                    _lastSender = ep.Address.ToString();
                }
                Interlocked.Exchange(ref _lastPacketTicks, Stopwatch.GetTimestamp());
            }
        }

        void Snapshot()
        {
            lock (_lock)
            {
                _bones.Clear();
                foreach (var kv in _bonesIn)
                    if (Enum.TryParse(kv.Key, out HumanBodyBones b) && b != HumanBodyBones.LastBone) _bones[b] = kv.Value;
                _blends.Clear();
                foreach (var kv in _blendApplied) _blends[kv.Key] = kv.Value;
            }
        }

        // Normalized world rotation of a bone = parent's normalized world rotation * its received local rotation.
        Quaternion NormalizedWorld(HumanBodyBones b)
        {
            if (_world.TryGetValue(b, out var w)) return w;
            int parent = HumanTrait.GetParentBone((int)b);
            var local = _bones.TryGetValue(b, out var q) ? q : Quaternion.identity;
            w = (parent >= 0 ? NormalizedWorld((HumanBodyBones)parent) : Quaternion.identity) * local;
            _world[b] = w;
            return w;
        }

        void ApplyBones()
        {
            _world.Clear();
            var root = _vrm.transform.rotation;
            foreach (var b in _bones.Keys)
            {
                if (!receiveAllBones && !UpperBones.Contains(b)) continue;
                var t = _vrm.Humanoid.GetBoneTransform(b);
                if (t == null || !driver.TryGetTPose(b, out var tposeRot, out _)) continue;
                t.rotation = root * NormalizedWorld(b) * tposeRot;
            }
        }

        void ApplyBlends()
        {
            var ex = _vrm.Runtime.Expression;
            foreach (var kv in _blends)
            {
                string name = kv.Key.ToLowerInvariant();
                if (Vrm0ToVrm1.TryGetValue(name, out var alias) && !_exprByName.ContainsKey(name)) name = alias;
                if (_exprByName.TryGetValue(name, out var key)) ex.SetWeight(key, Mathf.Clamp01(kv.Value));
            }
        }

        // ------------------------------------------------------------------ send

        void LateUpdate()
        {
            if (!sendEnabled || _vrm == null || driver == null)
            {
                if (_tx != null) StopSender();
                return;
            }
            if (_tx == null || _txHost != sendHost || _txPort != sendPort) StartSender();
            if (_tx == null) return;
            try { Send(); }
            catch (Exception e)
            {
                SendError = e.Message;
                Debug.LogWarning($"[Mystic] VMC send failed: {e.Message}");
                sendEnabled = false;
            }
        }

        void StartSender()
        {
            StopSender();
            SendError = null;
            try
            {
                var address = IPAddress.TryParse(sendHost, out var ip) ? ip : Dns.GetHostAddresses(sendHost)[0];
                _txTarget = new IPEndPoint(address, sendPort);
                _tx = new UdpClient();
                _txHost = sendHost;
                _txPort = sendPort;
            }
            catch (Exception e)
            {
                SendError = $"Cannot send to {sendHost}:{sendPort} ({e.Message})";
                sendEnabled = false;
                StopSender();
            }
        }

        void StopSender()
        {
            _tx?.Close();
            _tx = null;
            _txHost = null;
            _txPort = -1;
        }

        HumanBodyBones? PresentParent(HumanBodyBones b)
        {
            int p = HumanTrait.GetParentBone((int)b);
            while (p >= 0 && _vrm.Humanoid.GetBoneTransform((HumanBodyBones)p) == null) p = HumanTrait.GetParentBone(p);
            return p >= 0 ? (HumanBodyBones?)p : null;
        }

        void Send()
        {
            var root = _vrm.transform;
            var rootInv = Quaternion.Inverse(root.rotation);
            _writer.Begin();
            _writer.Add("/VMC/Ext/OK", 1);
            _writer.Add("/VMC/Ext/T", Time.time);
            var rp = root.localPosition;
            var rq = root.localRotation;
            _writer.Add("/VMC/Ext/Root/Pos", "root", rp.x, rp.y, rp.z, rq.x, rq.y, rq.z, rq.w);

            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var b = (HumanBodyBones)i;
                var t = _vrm.Humanoid.GetBoneTransform(b);
                if (t == null || !driver.TryGetTPose(b, out var tRot, out var tPos)) continue;

                var world = rootInv * t.rotation * Quaternion.Inverse(tRot);
                var parent = PresentParent(b);
                Quaternion local = world;
                Vector3 pos;
                if (parent.HasValue && driver.TryGetTPose(parent.Value, out var pRot, out var pPos))
                {
                    var pt = _vrm.Humanoid.GetBoneTransform(parent.Value);
                    var parentWorld = rootInv * pt.rotation * Quaternion.Inverse(pRot);
                    local = Quaternion.Inverse(parentWorld) * world;
                    pos = tPos - pPos; // normalized bones keep their T-pose offsets in root axes
                }
                else
                {
                    pos = root.InverseTransformPoint(t.position); // hips: live position
                }
                _writer.Add("/VMC/Ext/Bone/Pos", b.ToString(), pos.x, pos.y, pos.z, local.x, local.y, local.z, local.w);
            }

            foreach (var kv in _vrm.Runtime.Expression.GetWeights())
            {
                var key = kv.Key;
                string name = key.Preset == ExpressionPreset.custom ? key.Name
                    : Vrm1ToVrm0.TryGetValue(key.Preset, out var v0) ? v0 : key.Preset.ToString();
                _writer.Add("/VMC/Ext/Blend/Val", name, kv.Value);
            }
            _writer.Add("/VMC/Ext/Blend/Apply");

            _tx.Send(_writer.Buffer, _writer.Length, _txTarget);
        }
    }
}
