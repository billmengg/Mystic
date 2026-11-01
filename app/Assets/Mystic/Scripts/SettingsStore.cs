using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Saves every Mystic setting to settings.json and restores it at startup. Saving is automatic: once a second the
    /// current settings are compared with the last save and written if anything changed. A model's own
    /// .mystic.json (AvatarProfile) still overrides these when that model loads.
    /// Runs early so values are in place before the other components start.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class SettingsStore : MonoBehaviour
    {
        public AvatarDriver driver;
        public LookController look;
        public ViewSetup view;
        public LipSync lipSync;
        public ArmDriver arms;
        public HairDebug debug;
        public TrackerReceiver receiver;
        public VmcProtocol vmc;
        public SpoutOutput spout;
        public VtsPhoneSource phone;
        public NativeWindow window;
        public TrackerLauncher launcher;

        // Which public fields of each component are user settings. Runtime state (pause, face calibration values)
        // is deliberately left out.
        static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>
        {
            ["driver"] = new[]
            {
                "rotationGain", "neckShare", "torsoShare", "armOutDegrees", "headMinCutoff", "headBeta", "faceMinCutoff",
                "faceBeta", "gazeRangeDegrees", "smileToHappy", "renderFollow", "faceIntensity", "lostGrace", "handRelax",
                "breathing", "sway", "shoulderLift", "leanAmount", "leanLimit", "blinkOpenBelow", "blinkClosedAt",
                "mouthOpenFrom", "mouthFullAt", "maxHeadSpeed", "maxHeadAccel", "glitchSpeed", "hairStiffness",
                "hairGravity", "strandCollision", "moodFadeSpeed",
            },
            ["look"] = new[] { "flatten", "region", "keepDepth", "rimEnabled", "rimColor", "rimSharpness", "rimStrength" },
            ["view"] = new[]
            {
                "backgroundColor", "fieldOfView", "distance", "panX", "panY", "orbitYaw", "orbitPitch", "presets",
                "keyYaw", "keyPitch", "keyColor", "keyIntensity", "ambientColor", "ambientIntensity",
            },
            ["receiver"] = new[] { "mirror" },
            ["vmc"] = new[] { "receiveEnabled", "receivePort", "receiveAllBones", "sendEnabled", "sendHost", "sendPort" },
            ["spout"] = new[] { "spoutEnabled", "senderName", "transparent" },
            ["window"] = new[] { "alwaysOnTop", "windowWidth", "windowHeight" },
            ["tracker"] = new[] { "autoStart", "cameraIndex", "withPose", "withHands", "showPreview" },
            ["phone"] = new[]
            {
                "phoneEnabled", "phoneIp", "flipTurn", "flipNod", "flipTilt", "flipLeanSideways", "flipLeanForward",
                "phoneHotkeysSetMood",
            },
            ["lipSync"] = new[] { "micEnabled", "device", "mode", "gateDb", "rangeDb", "vowels" },
            ["arms"] = new[] { "trackArms", "trackHands", "follow", "blendSeconds" },
            ["debug"] = new[] { "spikeAccel" },
        };

        JObject _defaults;
        string _lastSaved;
        float _nextCheck;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");
        public string Status { get; private set; } = "";

        void Start()
        {
            _defaults = Capture();
            if (File.Exists(FilePath))
            {
                try
                {
                    Apply(JObject.Parse(File.ReadAllText(FilePath)));
                    Status = "Settings loaded";
                    Debug.Log($"[Mystic] Settings loaded from {FilePath}");
                }
                catch (Exception e)
                {
                    Status = "Settings file unreadable; using defaults";
                    Debug.LogWarning($"[Mystic] Could not read {FilePath}: {e.Message}");
                }
            }
            _lastSaved = Capture().ToString(Formatting.Indented);
        }

        void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            SaveIfChanged();
        }

        void OnApplicationQuit() => SaveIfChanged();

        void SaveIfChanged()
        {
            var json = Capture().ToString(Formatting.Indented);
            if (json == _lastSaved) return;
            try
            {
                File.WriteAllText(FilePath, json);
                _lastSaved = json;
                Status = $"Saved {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception e)
            {
                Status = "Could not save settings (see Console)";
                Debug.LogWarning($"[Mystic] Could not write {FilePath}: {e.Message}");
            }
        }

        public void ResetToDefaults()
        {
            if (_defaults == null) return;
            Apply(_defaults);
            Status = "Reset to defaults";
        }

        Dictionary<string, object> Targets() => new Dictionary<string, object>
        {
            ["driver"] = driver, ["look"] = look, ["view"] = view, ["lipSync"] = lipSync, ["arms"] = arms, ["debug"] = debug,
            ["receiver"] = receiver, ["vmc"] = vmc, ["spout"] = spout, ["phone"] = phone, ["window"] = window, ["tracker"] = launcher,
        };

        JObject Capture()
        {
            var root = new JObject();
            foreach (var (name, target) in Targets())
            {
                if (target == null) continue;
                var section = new JObject();
                foreach (var field in Fields[name])
                {
                    var fi = target.GetType().GetField(field);
                    if (fi != null) section[field] = ToToken(fi.GetValue(target));
                }
                root[name] = section;
            }
            root["hotkeys"] = new JObject
            {
                ["globalEnabled"] = Hotkeys.GlobalEnabled,
                ["globalModifier"] = Hotkeys.GlobalModifier.ToString(),
            };
            return root;
        }

        void Apply(JObject root)
        {
            foreach (var (name, target) in Targets())
            {
                if (target == null || !(root[name] is JObject section)) continue;
                foreach (var field in Fields[name])
                {
                    var fi = target.GetType().GetField(field);
                    if (fi == null || !section.TryGetValue(field, out var token)) continue;
                    try { fi.SetValue(target, FromToken(token, fi.FieldType)); }
                    catch (Exception e) { Debug.LogWarning($"[Mystic] Setting {name}.{field} ignored: {e.Message}"); }
                }
            }
            if (root["hotkeys"] is JObject hk)
            {
                if (hk["globalEnabled"] != null) Hotkeys.GlobalEnabled = (bool)hk["globalEnabled"];
                if (hk["globalModifier"] != null && Enum.TryParse((string)hk["globalModifier"], out Hotkeys.Modifier m))
                    Hotkeys.GlobalModifier = m;
            }
            // The camera only reads these at startup, so push them through when settings change later (reset).
            if (view != null) view.SetBackground(view.backgroundColor);
        }

        // Unity's Color can't go through Newtonsoft directly (its properties return Colors, which loops), so colours
        // are stored as [r, g, b, a]; enums as their names so the file stays readable.
        static JToken ToToken(object value)
        {
            switch (value)
            {
                case null: return JValue.CreateNull();
                case Color c: return new JArray(c.r, c.g, c.b, c.a);
                case Enum e: return e.ToString();
                default: return JToken.FromObject(value);
            }
        }

        static object FromToken(JToken token, Type type)
        {
            if (token.Type == JTokenType.Null) return null;
            if (type == typeof(Color))
            {
                var a = (JArray)token;
                return new Color((float)a[0], (float)a[1], (float)a[2], a.Count > 3 ? (float)a[3] : 1f);
            }
            if (type.IsEnum) return Enum.Parse(type, (string)token);
            return token.ToObject(type);
        }
    }
}
