using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UniVRM10;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mystic
{
    // Mystic avatar layer, stage 1: a "<model>.mystic.json" sidecar next to the .vrm. See format/MYSTIC_AVATAR.md.
    // Declarative only: the file describes actions, it never contains code.

    [Serializable]
    public class MysticAvatarFile
    {
        public int mystic = 1;
        public MysticPhysics physics;
        public MysticLook look;
        public MysticMotion motion;
        public MysticFace face;
        public List<MysticHotkey> hotkeys = new List<MysticHotkey>();
    }

    [Serializable] public class MysticPhysics { public float? hairGravity, hairStiffness; public bool? strandCollision; }
    [Serializable] public class MysticLook { public float? flatten, keepDepth; public string region; }
    [Serializable] public class MysticMotion { public float? maxHeadSpeed, maxHeadAccel, glitchSpeed; }
    [Serializable] public class MysticFace { public float? intensity; }

    [Serializable]
    public class MysticHotkey
    {
        public string key;        // "6", "G", "F1", "Numpad1" ... (Unity Input System key names)
        public string action;     // swapTexture | toggleExpression | toggleObject | setMood
        public string label;      // shown in the panel
        public string material;   // swapTexture: material name in the model
        public string texture;    // swapTexture: image path, relative to the .mystic.json
        public string expression; // toggleExpression: VRM preset name (happy, blink...) or custom name
        public string target;     // toggleObject: name of an object in the model hierarchy
        public string mood;       // setMood: Happy | Angry | Sad | Surprised | Relaxed | None
    }

    /// <summary>Loads, applies and saves the sidecar for whichever model is loaded, and runs its hotkeys.</summary>
    public class AvatarProfile : MonoBehaviour
    {
        public AvatarLoader loader;
        public AvatarDriver driver;
        public LookController look;

        // Keys Mystic already uses; a sidecar hotkey on one of these is skipped with a warning.
        static readonly HashSet<Key> Reserved = new HashSet<Key>
        {
            Key.C, Key.H, Key.P, Key.F2, Key.F3, Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
        };

        public MysticAvatarFile Current { get; private set; }
        public string SidecarPath { get; private set; }
        public string Status { get; private set; } = "No model loaded";
        public readonly List<(string key, string label)> Bindings = new List<(string, string)>();

        readonly List<(Key key, MysticHotkey hotkey)> _bindings = new List<(Key, MysticHotkey)>();
        readonly HashSet<MysticHotkey> _on = new HashSet<MysticHotkey>();
        readonly Dictionary<MysticHotkey, List<(Material mat, Texture main, Texture shade)>> _originals =
            new Dictionary<MysticHotkey, List<(Material, Texture, Texture)>>();
        readonly Dictionary<string, Texture2D> _loadedTextures = new Dictionary<string, Texture2D>();
        Vrm10Instance _vrm;

        public static string SidecarFor(string vrmPath) =>
            Path.Combine(Path.GetDirectoryName(vrmPath) ?? "", Path.GetFileNameWithoutExtension(vrmPath) + ".mystic.json");

        void Start()
        {
            if (loader == null) return;
            loader.Loaded += OnLoaded;
            if (loader.Avatar != null) OnLoaded(loader.Avatar);
        }

        void OnDestroy()
        {
            if (loader != null) loader.Loaded -= OnLoaded;
        }

        void OnLoaded(Vrm10Instance vrm)
        {
            _vrm = vrm;
            _bindings.Clear();
            Bindings.Clear();
            _on.Clear();
            _originals.Clear();
            Current = null;
            SidecarPath = loader.CurrentPath != null ? SidecarFor(loader.CurrentPath) : null;

            if (SidecarPath == null || !File.Exists(SidecarPath))
            {
                Status = "No settings file yet";
                return;
            }
            try
            {
                Current = JsonConvert.DeserializeObject<MysticAvatarFile>(File.ReadAllText(SidecarPath)) ?? new MysticAvatarFile();
                ApplySettings(Current);
                BindHotkeys(Current);
                Status = $"Loaded {Path.GetFileName(SidecarPath)} ({_bindings.Count} hotkeys)";
                Debug.Log($"[Mystic] {Status}");
            }
            catch (Exception e)
            {
                Status = "Settings file has an error (see Console)";
                Debug.LogError($"[Mystic] Could not read {SidecarPath}: {e.Message}");
            }
        }

        void ApplySettings(MysticAvatarFile f)
        {
            if (driver != null)
            {
                if (f.physics?.hairGravity is float g) driver.hairGravity = g;
                if (f.physics?.hairStiffness is float s) driver.hairStiffness = s;
                if (f.physics?.strandCollision is bool sc) driver.strandCollision = sc;
                if (f.motion?.maxHeadSpeed is float ms) driver.maxHeadSpeed = ms;
                if (f.motion?.maxHeadAccel is float ma) driver.maxHeadAccel = ma;
                if (f.motion?.glitchSpeed is float gs) driver.glitchSpeed = gs;
                if (f.face?.intensity is float fi) driver.faceIntensity = fi;
            }
            if (look != null)
            {
                if (f.look?.flatten is float fl) look.flatten = fl;
                if (f.look?.keepDepth is float kd) look.keepDepth = kd;
                if (f.look?.region != null && Enum.TryParse(f.look.region, true, out LookController.FlattenRegion r)) look.region = r;
            }
        }

        void BindHotkeys(MysticAvatarFile f)
        {
            if (f.hotkeys == null) return;
            foreach (var h in f.hotkeys)
            {
                if (h == null || string.IsNullOrEmpty(h.key)) continue;
                var name = h.key.Length == 1 && char.IsDigit(h.key[0]) ? "Digit" + h.key : h.key;
                if (!Enum.TryParse(name, true, out Key key) || key == Key.None)
                {
                    Debug.LogWarning($"[Mystic] Unknown hotkey '{h.key}' in {Path.GetFileName(SidecarPath)}");
                    continue;
                }
                if (Reserved.Contains(key))
                {
                    Debug.LogWarning($"[Mystic] Hotkey '{h.key}' is already used by Mystic; skipped.");
                    continue;
                }
                _bindings.Add((key, h));
                Bindings.Add((h.key, string.IsNullOrEmpty(h.label) ? h.action : h.label));
            }
        }

        void Update()
        {
            if (_vrm == null) return;
            foreach (var (key, h) in _bindings)
                if (Hotkeys.Pressed(key)) Run(h);
        }

        void Run(MysticHotkey h)
        {
            bool turnOn = !_on.Contains(h);
            switch (h.action)
            {
                case "swapTexture": SwapTexture(h, turnOn); break;
                case "toggleExpression": ToggleExpression(h, turnOn); break;
                case "toggleObject": ToggleObject(h); return; // follows the object's own state
                case "setMood":
                    if (driver != null && Enum.TryParse(h.mood, true, out AvatarDriver.Mood m)) driver.ToggleMood(m);
                    return;
                default:
                    Debug.LogWarning($"[Mystic] Unknown hotkey action '{h.action}'");
                    return;
            }
            if (turnOn) _on.Add(h); else _on.Remove(h);
        }

        void SwapTexture(MysticHotkey h, bool turnOn)
        {
            if (!turnOn)
            {
                if (_originals.TryGetValue(h, out var saved))
                    foreach (var (mat, main, shade) in saved)
                    {
                        mat.SetTexture("_MainTex", main);
                        mat.SetTexture("_ShadeTex", shade);
                    }
                return;
            }

            var tex = LoadTexture(h.texture);
            if (tex == null) return;
            var list = new List<(Material, Texture, Texture)>();
            foreach (var r in _vrm.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.name.Replace(" (Instance)", "") != h.material) continue;
                    var main = mat.GetTexture("_MainTex");
                    var shade = mat.HasProperty("_ShadeTex") ? mat.GetTexture("_ShadeTex") : null;
                    list.Add((mat, main, shade));
                    mat.SetTexture("_MainTex", tex);
                    if (shade == main && mat.HasProperty("_ShadeTex")) mat.SetTexture("_ShadeTex", tex);
                }
            if (list.Count == 0) Debug.LogWarning($"[Mystic] swapTexture: no material named '{h.material}' on this model");
            _originals[h] = list;
        }

        Texture2D LoadTexture(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            var path = Path.Combine(Path.GetDirectoryName(SidecarPath) ?? "", relative);
            if (_loadedTextures.TryGetValue(path, out var cached) && cached != null) return cached;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[Mystic] Texture not found: {path}");
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path)))
            {
                Debug.LogWarning($"[Mystic] Could not decode image: {path}");
                return null;
            }
            tex.name = Path.GetFileNameWithoutExtension(path);
            _loadedTextures[path] = tex;
            return tex;
        }

        void ToggleExpression(MysticHotkey h, bool turnOn)
        {
            if (string.IsNullOrEmpty(h.expression)) return;
            var key = Enum.TryParse(h.expression, true, out ExpressionPreset preset) && preset != ExpressionPreset.custom
                ? ExpressionKey.CreateFromPreset(preset)
                : ExpressionKey.CreateCustom(h.expression);
            _vrm.Runtime.Expression.SetWeight(key, turnOn ? 1f : 0f);
        }

        void ToggleObject(MysticHotkey h)
        {
            if (string.IsNullOrEmpty(h.target)) return;
            foreach (var t in _vrm.GetComponentsInChildren<Transform>(true))
                if (t.name == h.target)
                {
                    t.gameObject.SetActive(!t.gameObject.activeSelf);
                    return;
                }
            Debug.LogWarning($"[Mystic] toggleObject: no object named '{h.target}' on this model");
        }

        /// <summary>Writes the current physics/look/motion/face settings into this model's sidecar, keeping its hotkeys.</summary>
        public void SaveCurrentSettings()
        {
            if (SidecarPath == null) return;
            var f = Current ?? new MysticAvatarFile();
            if (driver != null)
            {
                f.physics = new MysticPhysics
                {
                    hairGravity = driver.hairGravity,
                    hairStiffness = driver.hairStiffness,
                    strandCollision = driver.HasStrandColliders ? driver.strandCollision : (bool?)null,
                };
                f.motion = new MysticMotion { maxHeadSpeed = driver.maxHeadSpeed, maxHeadAccel = driver.maxHeadAccel, glitchSpeed = driver.glitchSpeed };
                f.face = new MysticFace { intensity = driver.faceIntensity };
            }
            if (look != null)
                f.look = new MysticLook { flatten = look.flatten, keepDepth = look.keepDepth, region = look.region.ToString() };

            try
            {
                var json = JsonConvert.SerializeObject(f, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.WriteAllText(SidecarPath, json);
                Current = f;
                Status = $"Saved {Path.GetFileName(SidecarPath)}";
                Debug.Log($"[Mystic] {Status}");
            }
            catch (Exception e)
            {
                Status = "Save failed (see Console)";
                Debug.LogError($"[Mystic] Could not write {SidecarPath}: {e.Message}");
            }
        }
    }
}
