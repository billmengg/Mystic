using System;
using UniVRM10;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mystic
{
    /// <summary>
    /// VSeeFace-style view: the avatar on a plain background, a key light plus ambient light, and a camera framed on
    /// the head. Mouse wheel zooms, middle-drag pans, right-drag orbits (all ignored while the pointer is over a panel).
    /// Framing is stored as distance/orbit/pan relative to the head, so it carries over between models.
    /// </summary>
    public class ViewSetup : MonoBehaviour
    {
        public AvatarLoader loader;

        public Color backgroundColor = new Color(0.16f, 0.20f, 0.30f);

        [Header("Camera")]
        public float fieldOfView = 30f;
        public float distance = 1.6f;
        [Tooltip("Aim point below the head bone, in metres.")] public float aimBelowHead = 0.2f;
        public float panX, panY;            // metres, added to the aim point
        public float orbitYaw, orbitPitch;  // degrees

        [Serializable]
        public class CameraPreset
        {
            public bool saved;
            public float distance, fieldOfView, panX, panY, orbitYaw, orbitPitch;
        }

        public CameraPreset[] presets = { new CameraPreset(), new CameraPreset(), new CameraPreset() };

        [Header("Lighting")]
        [Tooltip("Key light direction left/right, degrees. 0 = straight from the camera.")]
        public float keyYaw = 15f;
        [Tooltip("Key light height, degrees above the avatar.")]
        public float keyPitch = 25f;
        public Color keyColor = Color.white;
        public float keyIntensity = 1f;
        public Color ambientColor = new Color(0.55f, 0.55f, 0.58f);
        public float ambientIntensity = 1f;

        Camera _cam;
        Light _key;
        Vector3 _aimBase = new Vector3(0f, 1.3f, 0f);
        bool _dragFromUi;

        public void SetBackground(Color c)
        {
            backgroundColor = c;
            if (_cam != null) _cam.backgroundColor = c;
        }

        public void SavePreset(int i)
        {
            if (i < 0 || i >= presets.Length) return;
            presets[i] = new CameraPreset
            {
                saved = true, distance = distance, fieldOfView = fieldOfView, panX = panX, panY = panY,
                orbitYaw = orbitYaw, orbitPitch = orbitPitch,
            };
        }

        public void LoadPreset(int i)
        {
            if (i < 0 || i >= presets.Length || presets[i] == null || !presets[i].saved) return;
            var p = presets[i];
            distance = p.distance;
            fieldOfView = p.fieldOfView;
            panX = p.panX;
            panY = p.panY;
            orbitYaw = p.orbitYaw;
            orbitPitch = p.orbitPitch;
        }

        public void ResetFraming()
        {
            distance = 1.6f;
            fieldOfView = 30f;
            panX = panY = orbitYaw = orbitPitch = 0f;
        }

        void Start()
        {
            _cam = Camera.main;
            if (_cam != null)
            {
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = backgroundColor;
            }
            SetupLighting();
            SetupAntiAliasing();

            if (loader != null)
            {
                loader.Loaded += Frame;
                if (loader.Avatar != null) Frame(loader.Avatar);
            }
        }

        void OnDestroy()
        {
            if (loader != null) loader.Loaded -= Frame;
        }

        void Update()
        {
            HandleMouse();
            ApplyLighting();
            PlaceCamera();
        }

        void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null || _cam == null) return;

            // Input over a panel belongs to the panel: the wheel scrolls it, not the camera. A drag that starts on a
            // panel stays the panel's until the button is released, even if the mouse leaves it.
            var pos = mouse.position.ReadValue();
            bool overUi = Ui.IsPointerOverUi(new Vector2(pos.x, Screen.height - pos.y));
            if (mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame) _dragFromUi = overUi;
            if (!mouse.rightButton.isPressed && !mouse.middleButton.isPressed) _dragFromUi = false;

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f && !overUi)
                distance = Mathf.Clamp(distance - Mathf.Sign(scroll) * 0.08f * distance, 0.4f, 6f);

            if (_dragFromUi) return;
            var delta = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed)
            {
                orbitYaw += delta.x * 0.2f;
                orbitPitch = Mathf.Clamp(orbitPitch - delta.y * 0.2f, -60f, 60f);
            }
            if (mouse.middleButton.isPressed)
            {
                float k = distance * 0.0012f;
                var move = _cam.transform.right * (-delta.x * k) + _cam.transform.up * (-delta.y * k);
                panX += move.x;
                panY += move.y;
            }
        }

        void Frame(Vrm10Instance vrm)
        {
            var head = vrm.Humanoid.Head;
            if (head == null) return;
            _aimBase = new Vector3(0f, head.position.y - aimBelowHead, 0f);
        }

        // The avatar faces +Z, so the camera sits on +Z looking back at it.
        void PlaceCamera()
        {
            if (_cam == null) return;
            _cam.fieldOfView = fieldOfView;
            var aim = _aimBase + new Vector3(panX, panY, 0f);
            var rot = Quaternion.Euler(orbitPitch, 180f + orbitYaw, 0f);
            _cam.transform.SetPositionAndRotation(aim - rot * Vector3.forward * distance, rot);
        }

        void SetupAntiAliasing()
        {
            // The template's URP asset leaves MSAA off, which makes VRM edges jagged.
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.msaaSampleCount = 4;
                urp.renderScale = 1f;
            }
            if (_cam != null && _cam.TryGetComponent<UniversalAdditionalCameraData>(out var data))
            {
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
            }
        }

        void SetupLighting()
        {
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;

            // The template scene's directional light points at the avatar's back; replace it.
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) l.enabled = false;

            var go = new GameObject("Key Light");
            go.transform.SetParent(transform, false);
            _key = go.AddComponent<Light>();
            _key.type = LightType.Directional;
            _key.shadows = LightShadows.None;
            ApplyLighting();
        }

        void ApplyLighting()
        {
            if (_key != null)
            {
                // 180 = shining from the camera side toward the avatar (which faces +Z).
                _key.transform.rotation = Quaternion.Euler(keyPitch, 180f + keyYaw, 0f);
                _key.color = keyColor;
                _key.intensity = keyIntensity;
            }
            RenderSettings.ambientLight = ambientColor * ambientIntensity;
        }
    }
}
