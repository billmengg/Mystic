using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UniVRM10;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mystic
{
    /// <summary>
    /// Debug overlay for spring-bone (hair) behaviour. F3 toggles it.
    /// Joints are measured relative to the head, so ordinary head turning doesn't register: only the hair's own
    /// motion does, which is where a twitch shows up. Can record 30 s of numbers to a CSV in Mystic/debug.
    /// </summary>
    [DefaultExecutionOrder(32000)] // after UniVRM has simulated the springs this frame
    public class HairDebug : MonoBehaviour
    {
        public AvatarLoader loader;
        public AvatarDriver driver;
        public bool show;

        [Tooltip("Joint acceleration relative to the head (m/s^2) above which a frame counts as a spike.")]
        public float spikeAccel = 40f;

        const float RecordSeconds = 30f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class JointTrack
        {
            public Transform T;
            public Vector3 World, Local, Vel;
            public float Accel;
            public bool Init;
        }

        sealed class ChainTrack
        {
            public string Name;
            public readonly List<JointTrack> Joints = new List<JointTrack>();
            public float TipSpeed, TipAccel, MaxAccel, PeakAccel;
            public int Spikes;
        }

        readonly List<ChainTrack> _chains = new List<ChainTrack>();
        Vrm10Instance _vrm;
        Transform _head;
        StreamWriter _csv;
        float _recordStart;

        public bool Recording => _csv != null;
        public string LastFile { get; private set; }

        void Start()
        {
            if (loader == null) return;
            loader.Loaded += OnLoaded;
            if (loader.Avatar != null) OnLoaded(loader.Avatar);
        }

        void OnDestroy()
        {
            if (loader != null) loader.Loaded -= OnLoaded;
            StopRecording();
        }

        void OnLoaded(Vrm10Instance vrm)
        {
            StopRecording();
            _vrm = vrm;
            _head = vrm.Humanoid.Head;
            _chains.Clear();
            foreach (var spring in vrm.SpringBone.Springs)
            {
                var chain = new ChainTrack { Name = string.IsNullOrEmpty(spring.Name) ? $"spring {_chains.Count}" : spring.Name };
                foreach (var joint in spring.Joints)
                    if (joint != null) chain.Joints.Add(new JointTrack { T = joint.transform });
                if (chain.Joints.Count > 0) _chains.Add(chain);
            }
        }

        void Update()
        {
            if (Hotkeys.Pressed(Key.F3)) show = !show;
        }

        public void ResetCounters()
        {
            foreach (var c in _chains)
            {
                c.Spikes = 0;
                c.PeakAccel = 0f;
            }
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (_vrm == null || _head == null || dt <= 0f) return;

            foreach (var c in _chains)
            {
                c.MaxAccel = 0f;
                foreach (var j in c.Joints)
                {
                    if (j.T == null) continue;
                    j.World = j.T.position;
                    var local = _head.InverseTransformPoint(j.World);
                    if (!j.Init)
                    {
                        j.Local = local;
                        j.Vel = Vector3.zero;
                        j.Init = true;
                        continue;
                    }
                    var vel = (local - j.Local) / dt;
                    j.Accel = ((vel - j.Vel) / dt).magnitude;
                    j.Local = local;
                    j.Vel = vel;
                    c.MaxAccel = Mathf.Max(c.MaxAccel, j.Accel);
                }
                var tip = c.Joints[c.Joints.Count - 1];
                c.TipSpeed = tip.Vel.magnitude;
                c.TipAccel = tip.Accel;
                c.PeakAccel = Mathf.Max(c.PeakAccel, c.MaxAccel);
                if (c.MaxAccel > spikeAccel) c.Spikes++;
            }

            if (Recording) WriteRow(dt);
        }

        // ------------------------------------------------------------------ recording

        public void StartRecording()
        {
            StopRecording();
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "debug"));
            Directory.CreateDirectory(dir);
            LastFile = Path.Combine(dir, $"hair_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _csv = new StreamWriter(LastFile, false, Encoding.UTF8);
            _recordStart = Time.unscaledTime;

            var header = new StringBuilder("t,dt,headRawDegPerSec,headShownDegPerSec,glitchesIgnored");
            foreach (var c in _chains)
            {
                var n = c.Name.Replace(",", "_");
                header.Append($",{n}.tipX,{n}.tipY,{n}.tipZ,{n}.tipSpeed,{n}.tipAccel,{n}.maxAccel");
            }
            _csv.WriteLine(header);
            Debug.Log($"[Mystic] Recording hair debug data to {LastFile}");
        }

        public void StopRecording()
        {
            if (_csv == null) return;
            _csv.Flush();
            _csv.Dispose();
            _csv = null;
            Debug.Log($"[Mystic] Hair debug recording saved: {LastFile}");
        }

        void WriteRow(float dt)
        {
            float t = Time.unscaledTime - _recordStart;
            var row = new StringBuilder();
            row.Append(t.ToString("0.000", Inv)).Append(',').Append(dt.ToString("0.0000", Inv));
            row.Append(',').Append((driver != null ? driver.LastRawHeadSpeed : 0f).ToString("0.0", Inv));
            row.Append(',').Append((driver != null ? driver.HeadShownSpeed : 0f).ToString("0.0", Inv));
            row.Append(',').Append(driver != null ? driver.GlitchesIgnored : 0);
            foreach (var c in _chains)
            {
                var tip = c.Joints[c.Joints.Count - 1];
                row.Append(',').Append(tip.Local.x.ToString("0.0000", Inv));
                row.Append(',').Append(tip.Local.y.ToString("0.0000", Inv));
                row.Append(',').Append(tip.Local.z.ToString("0.0000", Inv));
                row.Append(',').Append(c.TipSpeed.ToString("0.000", Inv));
                row.Append(',').Append(c.TipAccel.ToString("0.0", Inv));
                row.Append(',').Append(c.MaxAccel.ToString("0.0", Inv));
            }
            _csv.WriteLine(row);
            if (t >= RecordSeconds) StopRecording();
        }

        // ------------------------------------------------------------------ drawing

        void OnGUI()
        {
            if (!show || Ui.StreamMode) return;

            // Joint dots, in real screen pixels: green = calm, red = at or above the spike threshold.
            GUI.matrix = Matrix4x4.identity;
            var cam = Camera.main;
            if (cam != null)
            {
                foreach (var c in _chains)
                {
                    for (int i = 0; i < c.Joints.Count; i++)
                    {
                        var j = c.Joints[i];
                        var sp = cam.WorldToScreenPoint(j.World);
                        if (sp.z <= 0f) continue;
                        GUI.color = Color.Lerp(Color.green, Color.red, Mathf.Clamp01(j.Accel / spikeAccel));
                        GUI.DrawTexture(new Rect(sp.x - 3f, Screen.height - sp.y - 3f, 6f, 6f), Texture2D.whiteTexture);
                        if (i == c.Joints.Count - 1)
                        {
                            GUI.color = Color.white;
                            GUI.Label(new Rect(sp.x + 5f, Screen.height - sp.y - 9f, 160f, 18f), c.Name);
                        }
                    }
                }
                GUI.color = Color.white;
            }

            // Numbers panel on the right, laid out like the settings panel.
            float s = Mathf.Clamp(Screen.height / 720f, 0.6f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float w = 380f;
            var rect = new Rect(Screen.width / s - w - 12f, 12f, w, Screen.height / s - 24f);

            int totalSpikes = 0;
            foreach (var c in _chains) totalSpikes += c.Spikes;

            Ui.Panel(rect, () =>
            {
                Ui.Title("Debug", totalSpikes > 0 ? Ui.Warn : Ui.Good, totalSpikes > 0 ? $"{totalSpikes} spikes" : "calm");

                Ui.Section("Head");
                if (driver != null)
                {
                    Ui.Label($"Tracker speed  {driver.LastRawHeadSpeed,7:0} deg/s");
                    Ui.Label($"Shown speed    {driver.HeadShownSpeed,7:0} deg/s   (cap {driver.maxHeadSpeed:0})");
                    Ui.Label($"Glitch frames dropped  {driver.GlitchesIgnored}");
                }

                Ui.Section($"Hair chains  (relative to head, spike > {spikeAccel:0} m/s²)");
                if (_chains.Count == 0) Ui.Hint("This model has no spring bones.");
                foreach (var c in _chains)
                {
                    Ui.Label($"{c.Name}  ({c.Joints.Count} joints)");
                    Ui.Hint($"tip {c.TipSpeed:0.00} m/s   acc {c.TipAccel,5:0}   peak {c.PeakAccel,5:0}   spikes {c.Spikes}");
                }
                spikeAccel = Ui.Slider("Spike threshold (m/s²)", spikeAccel, 5f, 200f, "0");

                Ui.Gap(6);
                GUILayout.BeginHorizontal();
                if (Ui.Button("Reset counters")) ResetCounters();
                GUILayout.Space(6);
                if (Ui.Button(Recording ? "Stop recording" : $"Record {RecordSeconds:0}s", !Recording))
                {
                    if (Recording) StopRecording(); else StartRecording();
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(LastFile)) Ui.Hint((Recording ? "Recording to " : "Saved ") + LastFile);

                GUILayout.FlexibleSpace();
                Ui.Hint("F3 hides this panel. Dots on the hair: green calm, red spiking.");
            });
        }
    }
}
