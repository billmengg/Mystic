using System.Reflection;
using Klak.Spout;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Mystic
{
    /// <summary>
    /// Spout2 output for OBS (needs OBS's "Spout2 Capture" plugin) via Keijiro's KlakSpout. With "transparent" on, the
    /// background is sent as alpha 0, so OBS shows only the avatar: no chroma key needed.
    /// </summary>
    public class SpoutOutput : MonoBehaviour
    {
        public bool spoutEnabled;
        public string senderName = "Mystic";
        public bool transparent = true;

        SpoutSender _sender;
        SpoutResources _resources;
        Camera _cam;

        public bool Sending => _sender != null;
        public string Error { get; private set; }

        void Update()
        {
            if (_cam == null) _cam = Camera.main;
            if (spoutEnabled && _sender == null) Create();
            else if (!spoutEnabled && _sender != null)
            {
                Destroy(_sender);
                _sender = null;
            }
            if (_sender != null)
            {
                _sender.spoutName = string.IsNullOrWhiteSpace(senderName) ? "Mystic" : senderName;
                _sender.keepAlpha = transparent;
            }
        }

        // The Game view ignores alpha, so it still shows the background colour; the Spout feed gets it as transparent.
        void LateUpdate()
        {
            if (_cam == null) return;
            var c = _cam.backgroundColor;
            float a = spoutEnabled && transparent ? 0f : 1f;
            if (!Mathf.Approximately(c.a, a))
            {
                c.a = a;
                _cam.backgroundColor = c;
            }
        }

        void Create()
        {
            if (_cam == null)
            {
                Error = "No camera to capture";
                spoutEnabled = false;
                return;
            }
            // KlakSpout's sender normally gets its shader through the editor; set it up from code instead.
            var shader = Shader.Find("Hidden/Klak/Spout/Blit");
            if (shader == null)
            {
                Error = "KlakSpout's shader was not found. Is the package installed?";
                spoutEnabled = false;
                return;
            }
            _resources = ScriptableObject.CreateInstance<SpoutResources>();
            _resources.blitShader = shader;

            _sender = gameObject.AddComponent<SpoutSender>();
            _sender.SetResources(_resources);
            _sender.captureMethod = CaptureMethod.Camera;
            _sender.sourceCamera = _cam;
            _sender.keepAlpha = transparent;
            _sender.spoutName = string.IsNullOrWhiteSpace(senderName) ? "Mystic" : senderName;
            AllowAlphaOutput();
            Error = null;
            Debug.Log($"[Mystic] Spout2 sender '{_sender.spoutName}' started");
        }

        // URP throws alpha away in post-processing (anti-aliasing) unless its pipeline asset allows alpha output, and
        // that setting has no public setter. Without this, "transparent" would arrive as a solid background.
        static void AllowAlphaOutput()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)) return;
            var field = typeof(UniversalRenderPipelineAsset).GetField("m_AllowPostProcessAlphaOutput",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) field.SetValue(urp, true);
            else Debug.LogWarning("[Mystic] Could not enable URP alpha output; Spout transparency may not work.");
        }
    }
}
