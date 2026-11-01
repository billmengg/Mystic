using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Switches a loaded avatar's MToon materials to Mystic/Toon (a copy of UniVRM's URP MToon with extra hooks) and
    /// drives the "2D mode" flatten every frame. With flatten at 0 the avatar looks exactly as before.
    /// </summary>
    public class LookController : MonoBehaviour
    {
        public AvatarLoader loader;

        public enum FlattenRegion { Face, UpperBody, WholeBody }

        [Header("2D mode")]
        [Range(0f, 1f)] public float flatten = 0f;
        public FlattenRegion region = FlattenRegion.Face;
        [Tooltip("Fraction of depth kept at full flatten. Too low and stacked parts (eyes, hair) start to flicker.")]
        [Range(0.02f, 0.5f)] public float keepDepth = 0.1f;

        [Header("Rim light")]
        [Tooltip("Coloured glow on the avatar's edges. Off leaves each model's own rim settings alone.")]
        public bool rimEnabled;
        public Color rimColor = new Color(0.55f, 0.75f, 1f);
        [Tooltip("Higher = thinner rim.")]
        [Range(0.5f, 10f)] public float rimSharpness = 3f;
        [Range(0f, 1f)] public float rimStrength = 0.6f;

        static readonly int IdRimColor = Shader.PropertyToID("_RimColor");
        static readonly int IdRimPower = Shader.PropertyToID("_RimFresnelPower");
        static readonly int IdRimLift = Shader.PropertyToID("_RimLift");

        // The model's own rim values, so switching the rim off restores them.
        readonly List<(Material mat, Color color, float power, float lift)> _rimOriginals =
            new List<(Material, Color, float, float)>();
        bool _rimApplied;
        Color _lastRimColor;
        float _lastRimSharpness = -1f, _lastRimStrength = -1f;

        const string MToonUrp = "VRM10/Universal Render Pipeline/MToon10";

        static readonly int IdFlatten = Shader.PropertyToID("_MysticFlatten");
        static readonly int IdMinDepth = Shader.PropertyToID("_MysticFlattenMinDepth");
        static readonly int IdPivot = Shader.PropertyToID("_MysticFlattenPivot");
        static readonly int IdRadius = Shader.PropertyToID("_MysticFlattenRadius");
        static readonly int IdFalloff = Shader.PropertyToID("_MysticFlattenFalloff");
        static readonly int IdCamFwd = Shader.PropertyToID("_MysticCameraFwd");

        Shader _toon;
        Camera _cam;
        Vrm10Instance _vrm;
        Transform _head, _chest;
        float _headRadius = 0.11f;

        public int SwappedMaterials { get; private set; }
        public int OtherMaterials { get; private set; }

        void Start()
        {
            _toon = Shader.Find("Mystic/Toon");
            if (_toon == null) Debug.LogError("[Mystic] Shader Mystic/Toon not found; 2D mode is unavailable.");
            _cam = Camera.main;
            if (loader != null)
            {
                loader.Loaded += OnLoaded;
                if (loader.Avatar != null) OnLoaded(loader.Avatar);
            }
        }

        void OnDestroy()
        {
            if (loader != null) loader.Loaded -= OnLoaded;
        }

        void OnLoaded(Vrm10Instance vrm)
        {
            _vrm = vrm;
            var hum = vrm.Humanoid;
            _head = hum.Head;
            _chest = hum.UpperChest != null ? hum.UpperChest : hum.Chest;
            if (hum.LeftEye != null && hum.RightEye != null)
                _headRadius = Mathf.Clamp(Vector3.Distance(hum.LeftEye.position, hum.RightEye.position) * 1.7f, 0.07f, 0.16f);

            SwappedMaterials = 0;
            _rimOriginals.Clear();
            _rimApplied = false;
            OtherMaterials = 0;
            if (_toon == null) return;

            var seen = new HashSet<Material>();
            var others = new List<string>();
            foreach (var r in vrm.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    if (m.shader.name == MToonUrp)
                    {
                        // Same properties and keywords, so the swap is lossless. Keep the render queue explicitly,
                        // since MToon sets a custom one for cutout/transparent materials.
                        int queue = m.renderQueue;
                        m.shader = _toon;
                        m.renderQueue = queue;
                        SwappedMaterials++;
                        _rimOriginals.Add((m, m.GetColor(IdRimColor), m.GetFloat(IdRimPower), m.GetFloat(IdRimLift)));
                    }
                    else
                    {
                        OtherMaterials++;
                        others.Add($"{m.name} ({m.shader.name})");
                    }
                }
            }
            Debug.Log($"[Mystic] Look: {SwappedMaterials} materials switched to Mystic/Toon" +
                      (others.Count > 0 ? $"; not MToon, so 2D mode skips them: {string.Join(", ", others)}" : ""));
        }

        void UpdateRim()
        {
            if (rimEnabled)
            {
                if (_rimApplied && rimColor == _lastRimColor && Mathf.Approximately(rimSharpness, _lastRimSharpness)
                    && Mathf.Approximately(rimStrength, _lastRimStrength)) return;
                foreach (var (mat, _, _, _) in _rimOriginals)
                {
                    if (mat == null) continue;
                    mat.SetColor(IdRimColor, rimColor * rimStrength);
                    mat.SetFloat(IdRimPower, rimSharpness);
                    mat.SetFloat(IdRimLift, 0f);
                }
                _rimApplied = true;
                _lastRimColor = rimColor;
                _lastRimSharpness = rimSharpness;
                _lastRimStrength = rimStrength;
            }
            else if (_rimApplied)
            {
                foreach (var (mat, color, power, lift) in _rimOriginals)
                {
                    if (mat == null) continue;
                    mat.SetColor(IdRimColor, color);
                    mat.SetFloat(IdRimPower, power);
                    mat.SetFloat(IdRimLift, lift);
                }
                _rimApplied = false;
            }
        }

        void LateUpdate()
        {
            UpdateRim();
            if (_cam == null) _cam = Camera.main;
            Shader.SetGlobalFloat(IdFlatten, _vrm != null ? flatten : 0f);
            if (_vrm == null || _cam == null || _head == null) return;

            Shader.SetGlobalFloat(IdMinDepth, keepDepth);
            Shader.SetGlobalVector(IdCamFwd, _cam.transform.forward);

            // The head bone sits at the base of the skull, so the face centre is a little above it.
            var up = _vrm.transform.up;
            var face = _head.position + up * _headRadius * 0.7f;
            var chest = _chest != null ? _chest.position : _head.position - up * _headRadius * 3f;

            Vector3 pivot;
            float radius, falloff;
            switch (region)
            {
                case FlattenRegion.Face:
                    pivot = face; radius = _headRadius * 1.1f; falloff = _headRadius;
                    break;
                case FlattenRegion.UpperBody:
                    pivot = Vector3.Lerp(face, chest, 0.5f); radius = Vector3.Distance(face, chest) * 0.5f + _headRadius * 1.5f; falloff = _headRadius * 2f;
                    break;
                default:
                    pivot = chest; radius = 5f; falloff = 0.1f;
                    break;
            }
            Shader.SetGlobalVector(IdPivot, pivot);
            Shader.SetGlobalFloat(IdRadius, radius);
            Shader.SetGlobalFloat(IdFalloff, falloff);
        }
    }
}
