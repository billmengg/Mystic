using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mystic
{
    /// <summary>Settings panel. Press H to hide or show it (hide it before capturing the window in OBS).</summary>
    public class SettingsPanel : MonoBehaviour
    {
        public AvatarDriver driver;
        public ViewSetup view;
        public LookController look;
        public AvatarProfile profile;
        public HairDebug debug;
        public LipSync lipSync;
        public ArmDriver arms;
        public SettingsStore store;
        public VmcProtocol vmc;
        public SpoutOutput spout;
        public VtsPhoneSource phone;
        public NativeWindow window;
        public TrackerLauncher launcher;

        const float PanelWidth = 360f;
        static readonly string[] Tabs = { "Model", "Tracking", "Avatar", "Look", "App" };

        static readonly Color[] LightColors =
        {
            Color.white, new Color(1f, 0.9f, 0.78f), new Color(0.8f, 0.88f, 1f), new Color(1f, 0.8f, 0.9f),
            new Color(0.75f, 1f, 0.95f),
        };

        static readonly Color[] AmbientColors =
        {
            new Color(0.55f, 0.55f, 0.58f), new Color(0.6f, 0.52f, 0.45f), new Color(0.45f, 0.52f, 0.65f),
            new Color(0.5f, 0.42f, 0.6f), new Color(0.3f, 0.3f, 0.33f),
        };

        static readonly Color[] RimColors =
        {
            new Color(0.55f, 0.75f, 1f), new Color(1f, 0.45f, 0.75f), new Color(0.7f, 0.5f, 1f), new Color(1f, 0.65f, 0.3f),
            Color.white,
        };

        static readonly Color[] Backgrounds =
        {
            new Color(0.16f, 0.20f, 0.30f), // default
            new Color(0f, 1f, 0f),          // green screen
            new Color(0f, 0.2f, 1f),        // blue screen
            Color.black,
            Color.white,
            new Color(0.55f, 0.55f, 0.58f),
        };

        bool _visible = true;
        bool _streamArmed;
        bool _openPicker; // the file dialog blocks, so it runs from Update, never inside OnGUI
        int _tab;
        Vector2 _scroll;
        List<string> _models;

        void Update()
        {
            if (Hotkeys.Pressed(Key.H))
            {
                if (Ui.StreamMode) Ui.StreamMode = false;
                else _visible = !_visible;
            }
            if (Hotkeys.Pressed(Key.F2)) Ui.StreamMode = !Ui.StreamMode;

            // Stream mode: any click inside Mystic's window brings the overlays back. "Armed" skips the click that
            // turned stream mode on.
            if (Ui.StreamMode && _streamArmed && Application.isFocused)
            {
                var m = Mouse.current;
                if (m != null && m.leftButton.wasPressedThisFrame)
                {
                    var p = m.position.ReadValue();
                    if (p.x >= 0 && p.y >= 0 && p.x <= Screen.width && p.y <= Screen.height)
                    {
                        Ui.StreamMode = false;
                        _visible = true;
                    }
                }
            }
            _streamArmed = Ui.StreamMode;

            if (_openPicker)
            {
                _openPicker = false;
                var path = NativeWindow.OpenFile("Choose a VRM model", "VRM models (*.vrm)\0*.vrm\0All files (*.*)\0*.*\0\0");
                if (!string.IsNullOrEmpty(path) && driver != null && driver.loader != null)
                {
                    _ = driver.loader.AddModel(path);
                    _models = null; // refresh the list
                }
            }
        }

        void OnGUI()
        {
            if (Ui.StreamMode || driver == null) return;

            // Lay out as if the window were 720 px tall, then scale to the real size.
            float s = Mathf.Clamp(Screen.height / 720f, 0.6f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float height = Screen.height / s - 24f;

            // Corner controls, top right: always-on-top pin and stream mode.
            float right = Screen.width / s - 12f;
            if (window != null)
            {
                string pinTip = NativeWindow.Supported
                    ? (window.alwaysOnTop ? "Always on top: on" : "Always on top: off")
                    : "Always on top (works in the built app)";
                if (Ui.IconButton(new Rect(right - 32f, 12f, 32f, 32f), Ui.Icon.Pin, window.alwaysOnTop, pinTip))
                    window.alwaysOnTop = !window.alwaysOnTop;
            }
            if (Ui.IconButton(new Rect(right - 72f, 12f, 32f, 32f), Ui.Icon.StreamMode, false,
                    "Stream mode: hide everything (F2)"))
                Ui.StreamMode = true;

            if (!_visible)
            {
                Ui.Panel(new Rect(12, 12, 150, 60), () =>
                {
                    if (Ui.Button("Settings  (H)")) _visible = true;
                });
                return;
            }

            Ui.Panel(new Rect(12, 12, PanelWidth, height), () =>
            {
                var (dot, text) = StatusLine();
                Ui.Title("Mystic", dot, text);
                Ui.Gap(4);
                GUILayout.BeginHorizontal();
                if (Ui.Button("Hide  (H)")) _visible = false;
                GUILayout.Space(6);
                if (Ui.Button("Stream mode  (F2)", true)) Ui.StreamMode = true;
                GUILayout.EndHorizontal();
                Ui.Gap(6);
                _tab = Ui.Tabs(Tabs, _tab);
                Ui.Gap(4);

                _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUIStyle.none);
                switch (_tab)
                {
                    case 0: ModelTab(); break;
                    case 1: TrackingTab(); break;
                    case 2: AvatarTab(); break;
                    case 3: LookTab(); break;
                    case 4: AppTab(); break;
                }
                GUILayout.EndScrollView();

                GUILayout.FlexibleSpace();
                Ui.Hint("Stream mode hides everything; click anywhere in Mystic to bring it back.");
            });
        }

        (Color, string) StatusLine()
        {
            switch (driver.State)
            {
                case AvatarDriver.TrackerState.Tracking: return (Ui.Good, "Tracking");
                case AvatarDriver.TrackerState.NoFace: return (Ui.Warn, "No face");
                default: return (Ui.Bad, "No tracker");
            }
        }

        void ModelTab()
        {
            var loader = driver.loader;
            Ui.Section("Avatar");
            if (loader == null) { Ui.Hint("No loader."); return; }

            if (_models == null) _models = AvatarLoader.ListModels();
            foreach (var path in _models)
            {
                bool current = path == loader.CurrentPath;
                if (Ui.ListRow(System.IO.Path.GetFileNameWithoutExtension(path), current) && !current && !loader.Loading)
                    _ = loader.Load(path);
                Ui.Gap(4);
            }
            if (_models.Count == 0) Ui.Hint("No .vrm files found in the Models folder.");
            Ui.Gap(2);
            if (Ui.Button("Add model from file...", true)) _openPicker = true;
            if (loader.Loading) Ui.Hint("Loading...");

            Ui.Gap(6);
            GUILayout.BeginHorizontal();
            if (Ui.Button("Refresh")) _models = AvatarLoader.ListModels();
            GUILayout.Space(6);
            if (Ui.Button("Open folder"))
            {
                System.IO.Directory.CreateDirectory(AvatarLoader.ModelsDirectory);
                Application.OpenURL("file:///" + AvatarLoader.ModelsDirectory.Replace('\\', '/'));
            }
            GUILayout.EndHorizontal();

            if (profile != null && loader.CurrentPath != null)
            {
                Ui.Section("Model settings file");
                Ui.Hint(profile.Status);
                Ui.Gap(4);
                if (Ui.Button("Save current settings to this model")) profile.SaveCurrentSettings();
                Ui.Hint("Saves hair, 2D mode, motion gate and face intensity next to the .vrm, so they load with it.");
                if (profile.Bindings.Count > 0)
                {
                    Ui.Section("Model hotkeys");
                    foreach (var (key, label) in profile.Bindings) Ui.Label($"{key}   {label}");
                }
            }
        }

        void TrackingTab()
        {
            Ui.Section("Head calibration");
            Ui.Hint("Look straight at the camera in a neutral pose.");
            Ui.Gap(4);
            if (Ui.Button("Calibrate head  (C)", true)) driver.Calibrate();
            if (driver.receiver != null)
            {
                driver.receiver.mirror = Ui.Toggle("Mirror movement", driver.receiver.mirror);
                Ui.Hint(driver.receiver.mirror
                    ? "Like a mirror: raise your right hand and the hand on that side of the screen goes up."
                    : "Like a puppet facing you: your right hand moves the avatar's right hand.");
            }

            Ui.Section("Face calibration (optional)");
            Ui.Hint("Records your relaxed face so it can be subtracted from the tracking. Separate from head calibration.");
            Ui.Gap(4);
            GUILayout.BeginHorizontal();
            if (Ui.Button("Calibrate face")) driver.CalibrateFace();
            if (driver.HasFaceBaseline)
            {
                GUILayout.Space(6);
                if (Ui.Button("Clear")) driver.ClearFaceCalibration();
            }
            GUILayout.EndHorizontal();
            if (driver.HasFaceBaseline)
                driver.useFaceBaseline = Ui.Toggle("Use face calibration", driver.useFaceBaseline);

            Ui.Section("Smoothing");
            driver.headMinCutoff = Ui.Slider("Head  (low = smoother)", driver.headMinCutoff, 0.2f, 6f);
            driver.faceMinCutoff = Ui.Slider("Face  (low = smoother)", driver.faceMinCutoff, 0.5f, 10f);
            if (driver.PerfectSync)
                driver.faceIntensity = Ui.Slider("Face intensity", driver.faceIntensity, 0.5f, 2f);

            Ui.Section("Blink & mouth");
            Ui.Hint("Lower 'closed at' if the avatar's eyes never fully close; raise 'open below' if it squints.");
            driver.blinkOpenBelow = Ui.Slider("Eye open below", driver.blinkOpenBelow, 0f, 0.5f);
            driver.blinkClosedAt = Ui.Slider("Eye closed at", driver.blinkClosedAt, 0.3f, 1f);
            driver.mouthOpenFrom = Ui.Slider("Mouth opens from", driver.mouthOpenFrom, 0f, 0.3f);
            driver.mouthFullAt = Ui.Slider("Mouth fully open at", driver.mouthFullAt, 0.2f, 1f);

            LipSyncSection();

            Ui.Section("When tracking is lost");
            Ui.Hint("The avatar holds its last pose and keeps breathing and blinking until you are back.");
            driver.lostGrace = Ui.Slider("Count as lost after (s)", driver.lostGrace, 0f, 2f);
            driver.trackingPaused = Ui.Toggle("Pause tracking  (P)", driver.trackingPaused);
        }

        void LipSyncSection()
        {
            if (lipSync == null) return;
            Ui.Section("Microphone lip sync");
            lipSync.micEnabled = Ui.Toggle("Use microphone", lipSync.micEnabled);
            if (!string.IsNullOrEmpty(lipSync.Error)) Ui.Hint(lipSync.Error);
            if (!lipSync.micEnabled) return;

            if (Ui.ListRow("System default", string.IsNullOrEmpty(lipSync.device))) lipSync.device = null;
            Ui.Gap(2);
            foreach (var d in Microphone.devices)
            {
                if (Ui.ListRow(d, lipSync.device == d)) lipSync.device = d;
                Ui.Gap(2);
            }
            Ui.Meter("Level", lipSync.Level);
            lipSync.gateDb = Ui.Slider("Noise gate (dB)  (raise if noise moves the mouth)", lipSync.gateDb, -70f, -20f, "0");
            lipSync.vowels = Ui.Toggle("Vowel shapes (A I U E O)", lipSync.vowels);
            GUILayout.BeginHorizontal();
            if (Ui.Chip("Blend with camera", lipSync.mode == LipSync.Mode.BlendWithCamera)) lipSync.mode = LipSync.Mode.BlendWithCamera;
            GUILayout.Space(4);
            if (Ui.Chip("Mic only", lipSync.mode == LipSync.Mode.MicOnly)) lipSync.mode = LipSync.Mode.MicOnly;
            GUILayout.EndHorizontal();
            Ui.Hint(lipSync.mode == LipSync.Mode.BlendWithCamera
                ? "The mic fills in when the camera can't see your mouth open."
                : "Mouth shape comes only from your voice; smiles still come from the camera.");
        }

        void AvatarTab()
        {
            Ui.Section("Body");
            driver.rotationGain = Ui.Slider("Head rotation", driver.rotationGain, 0f, 2f);
            driver.neckShare = Ui.Slider("Neck", driver.neckShare, 0f, 1f);
            driver.torsoShare = Ui.Slider("Body follow", driver.torsoShare, 0f, 0.6f);
            driver.gazeRangeDegrees = Ui.Slider("Eye movement", driver.gazeRangeDegrees, 0f, 60f, "0");
            if (driver.HasFingers)
                driver.handRelax = Ui.Slider("Relaxed fingers", driver.handRelax, 0f, 1.5f);

            if (arms != null)
            {
                Ui.Section("Arms & hands");
                arms.trackArms = Ui.Toggle("Track arms", arms.trackArms);
                arms.trackHands = Ui.Toggle("Track hands & fingers", arms.trackHands);
                arms.follow = Ui.Slider("Follow speed  (low = smoother)", arms.follow, 2f, 30f, "0");
                string pose = arms.PoseSeen ? "yes" : "no";
                string hands = arms.LeftHandSeen && arms.RightHandSeen ? "both"
                    : arms.LeftHandSeen ? "left" : arms.RightHandSeen ? "right" : "none";
                Ui.Hint($"Receiving: arms {pose}, hands {hands}. Needs the tracker started with --pose and --hands " +
                        "(.\\run_tracker.ps1 --pose --hands) and your arms in view.");
            }

            Ui.Section("Idle & lean");
            driver.breathing = Ui.Slider("Breathing", driver.breathing, 0f, 2f);
            driver.shoulderLift = Ui.Slider("Shoulder rise (cm)", driver.shoulderLift * 100f, 0f, 2f) / 100f;
            driver.sway = Ui.Slider("Sway", driver.sway, 0f, 2f);
            driver.leanAmount = Ui.Slider("Lean with head position", driver.leanAmount, 0f, 1.5f);

            if (driver.HasSprings)
            {
                Ui.Section("Hair physics");
                driver.hairGravity = Ui.Slider("Gravity", driver.hairGravity, 0f, 2f);
                driver.hairStiffness = Ui.Slider("Stiffness  (low = floppier)", driver.hairStiffness, 0.05f, 2f);
                if (driver.HasStrandColliders)
                    driver.strandCollision = Ui.Toggle("Hair collides with itself", driver.strandCollision);
                if (Ui.Button("Reset hair")) driver.ResetSprings();
                if (debug != null && Ui.Button(debug.show ? "Hide hair debug  (F3)" : "Show hair debug  (F3)"))
                    debug.show = !debug.show;
            }

            Ui.Section("Motion gate");
            Ui.Hint("Limits how fast the head can turn and speed up, so tracking glitches don't jolt the hair.");
            driver.maxHeadSpeed = Ui.Slider("Max turn speed (deg/s)", driver.maxHeadSpeed, 90f, 1080f, "0");
            driver.maxHeadAccel = Ui.Slider("Max acceleration (deg/s²)", driver.maxHeadAccel, 300f, 8000f, "0");
            driver.glitchSpeed = Ui.Slider("Glitch threshold (deg/s)", driver.glitchSpeed, 300f, 2000f, "0");
            if (driver.GlitchesIgnored > 0) Ui.Hint($"Glitch frames dropped this session: {driver.GlitchesIgnored}");

            Ui.Section("Mood  (keys 1-5, 0 clears)");
            var moods = new[]
            {
                AvatarDriver.Mood.Happy, AvatarDriver.Mood.Angry, AvatarDriver.Mood.Sad,
                AvatarDriver.Mood.Surprised, AvatarDriver.Mood.Relaxed,
            };
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (int i = row * 3; i < Mathf.Min(moods.Length, row * 3 + 3); i++)
                {
                    bool on = driver.CurrentMood == moods[i];
                    if (Ui.Chip($"{i + 1}  {moods[i]}", on)) driver.ToggleMood(moods[i]);
                    GUILayout.Space(4);
                }
                GUILayout.EndHorizontal();
                Ui.Gap(4);
            }
        }

        void LookTab()
        {
            if (look != null)
            {
                Ui.Section("2D mode");
                look.flatten = Ui.Slider("Flatten", look.flatten, 0f, 1f);
                GUILayout.BeginHorizontal();
                if (Ui.Chip("Face", look.region == LookController.FlattenRegion.Face)) look.region = LookController.FlattenRegion.Face;
                GUILayout.Space(4);
                if (Ui.Chip("Upper body", look.region == LookController.FlattenRegion.UpperBody)) look.region = LookController.FlattenRegion.UpperBody;
                GUILayout.Space(4);
                if (Ui.Chip("Whole", look.region == LookController.FlattenRegion.WholeBody)) look.region = LookController.FlattenRegion.WholeBody;
                GUILayout.EndHorizontal();
                Ui.Gap(4);
                look.keepDepth = Ui.Slider("Keep depth  (raise if parts flicker)", look.keepDepth, 0.02f, 0.5f);
                if (look.OtherMaterials > 0)
                    Ui.Hint($"{look.OtherMaterials} non-toon material(s) on this model are not flattened.");
            }

            if (view == null) return;

            Ui.Section("Background");
            int picked = Ui.Swatches(Backgrounds, view.backgroundColor);
            if (picked >= 0) view.SetBackground(Backgrounds[picked]);
            Ui.Gap(4);
            Ui.Hint("Green and blue are for chroma keying in OBS.");

            Ui.Section("Key light");
            int key = Ui.Swatches(LightColors, view.keyColor);
            if (key >= 0) view.keyColor = LightColors[key];
            Ui.Gap(4);
            view.keyIntensity = Ui.Slider("Brightness", view.keyIntensity, 0f, 2f);
            view.keyYaw = Ui.Slider("Direction  (left / right)", view.keyYaw, -90f, 90f, "0");
            view.keyPitch = Ui.Slider("Height", view.keyPitch, -30f, 80f, "0");

            Ui.Section("Ambient light");
            int amb = Ui.Swatches(AmbientColors, view.ambientColor);
            if (amb >= 0) view.ambientColor = AmbientColors[amb];
            Ui.Gap(4);
            view.ambientIntensity = Ui.Slider("Brightness", view.ambientIntensity, 0f, 2f);

            if (look != null)
            {
                Ui.Section("Rim light");
                look.rimEnabled = Ui.Toggle("Glow on the edges", look.rimEnabled);
                if (look.rimEnabled)
                {
                    int rim = Ui.Swatches(RimColors, look.rimColor);
                    if (rim >= 0) look.rimColor = RimColors[rim];
                    Ui.Gap(4);
                    look.rimStrength = Ui.Slider("Strength", look.rimStrength, 0f, 1f);
                    look.rimSharpness = Ui.Slider("Sharpness  (high = thin edge)", look.rimSharpness, 0.5f, 10f);
                }
            }

            Ui.Section("Camera");
            view.fieldOfView = Ui.Slider("Field of view", view.fieldOfView, 10f, 60f, "0");
            view.distance = Ui.Slider("Distance", view.distance, 0.4f, 6f);
            view.panX = Ui.Slider("Left / right", view.panX, -1f, 1f);
            view.panY = Ui.Slider("Up / down", view.panY, -1f, 1f);
            for (int i = 0; i < view.presets.Length; i++)
            {
                bool saved = view.presets[i] != null && view.presets[i].saved;
                GUILayout.BeginHorizontal();
                if (Ui.Button(saved ? $"Load preset {i + 1}" : $"Preset {i + 1} (empty)") && saved) view.LoadPreset(i);
                GUILayout.Space(6);
                if (Ui.Button("Save here")) view.SavePreset(i);
                GUILayout.EndHorizontal();
                Ui.Gap(4);
            }
            if (Ui.Button("Reset framing")) view.ResetFraming();
            Ui.Hint("Mouse: wheel zoom, right-drag orbit, middle-drag pan.");
        }

        static bool IsLocal(string host) =>
            host == "127.0.0.1" || host == "localhost" || host == "::1";

        void AppTab()
        {
            if (launcher != null)
            {
                Ui.Section("Webcam tracker");
                launcher.autoStart = Ui.Toggle("Run the webcam tracker", launcher.autoStart);
                if (launcher.autoStart)
                {
                    var cams = TrackerLauncher.CameraNames;
                    if (cams.Length == 0) Ui.Hint("No cameras found.");
                    for (int i = 0; i < cams.Length; i++)
                    {
                        if (Ui.ListRow($"{i}: {cams[i]}", launcher.cameraIndex == i)) launcher.cameraIndex = i;
                        Ui.Gap(2);
                    }
                    launcher.withPose = Ui.Toggle("Track arms", launcher.withPose);
                    launcher.withHands = Ui.Toggle("Track hands & fingers", launcher.withHands);
                    launcher.showPreview = Ui.Toggle("Show tracker camera window", launcher.showPreview);
                    Ui.Hint(launcher.Status);
                    if (Ui.Button("Restart tracker")) launcher.Restart();
                    Ui.Hint("Arms and hands cost frame rate. If the wrong camera opens, pick another one.");
                }
            }

            if (phone != null)
            {
                Ui.Section("iPhone (VTube Studio)");
                phone.phoneEnabled = Ui.Toggle("Use iPhone face tracking", phone.phoneEnabled);
                if (!string.IsNullOrEmpty(phone.Error)) Ui.Hint(phone.Error);
                if (phone.phoneEnabled)
                {
                    phone.phoneIp = Ui.TextField("Phone IP", phone.phoneIp);
                    Ui.Hint(phone.Connected
                        ? $"Connected: {phone.PacketsPerSecond} updates/s. Face comes from the phone; arms still from the webcam tracker."
                        : $"Waiting. In VTube Studio on the phone: Settings, turn on '3rd Party PC Clients', and enter the IP it shows. " +
                          $"If the phone says connected but this stays waiting, Windows Firewall is blocking UDP port {phone.replyPort}.");
                    Ui.Hint("If a direction is backwards, flip it (then press C to recalibrate):");
                    phone.flipTurn = Ui.Toggle("Flip head turn", phone.flipTurn);
                    phone.flipNod = Ui.Toggle("Flip nod", phone.flipNod);
                    phone.flipTilt = Ui.Toggle("Flip head tilt", phone.flipTilt);
                    phone.flipLeanSideways = Ui.Toggle("Flip lean sideways", phone.flipLeanSideways);
                    phone.flipLeanForward = Ui.Toggle("Flip lean forward", phone.flipLeanForward);
                    phone.phoneHotkeysSetMood = Ui.Toggle("Phone buttons 1-5 set moods (6 clears)", phone.phoneHotkeysSetMood);
                }
            }

            Ui.Section("Stream mode");
            Ui.Hint("Hides every overlay so Mystic can be captured clean. Click anywhere in Mystic (or press F2 / H) to bring them back.");
            Ui.Gap(4);
            if (Ui.Button("Enter stream mode  (F2)", true)) Ui.StreamMode = true;

            if (spout != null)
            {
                Ui.Section("OBS output (Spout2)");
                spout.spoutEnabled = Ui.Toggle("Send to OBS", spout.spoutEnabled);
                if (!string.IsNullOrEmpty(spout.Error)) Ui.Hint(spout.Error);
                if (spout.spoutEnabled)
                {
                    spout.senderName = Ui.TextField("Source name", spout.senderName);
                    spout.transparent = Ui.Toggle("Transparent background", spout.transparent);
                    Ui.Hint(spout.Sending
                        ? $"Sending as '{spout.senderName}'. In OBS: add a Spout2 Capture source and pick it."
                        : "Starting...");
                }
                else
                {
                    Ui.Hint("Needs the OBS Spout2 plugin. Lossless, and with a transparent background no chroma key is needed.");
                }
            }

            if (vmc != null)
            {
                Ui.Section("VMC protocol");
                vmc.receiveEnabled = Ui.Toggle("Receive (e.g. iPhone apps)", vmc.receiveEnabled);
                if (!string.IsNullOrEmpty(vmc.ReceiveError)) Ui.Hint(vmc.ReceiveError);
                if (vmc.receiveEnabled)
                {
                    vmc.receivePort = Ui.IntField("Listen on port", vmc.receivePort, 1, 65535);
                    vmc.receiveAllBones = Ui.Toggle("Use all bones (full-body apps)", vmc.receiveAllBones);
                    Ui.Hint(vmc.Receiving
                        ? $"Receiving from {vmc.LastSender}: {vmc.BonesReceived} bones, {vmc.BlendsReceived} expressions."
                        : "Waiting for data. Point the app at this PC's IP address and this port.");
                }
                Ui.Gap(4);
                vmc.sendEnabled = Ui.Toggle("Send to another app", vmc.sendEnabled);
                if (!string.IsNullOrEmpty(vmc.SendError)) Ui.Hint(vmc.SendError);
                if (vmc.sendEnabled)
                {
                    vmc.sendHost = Ui.TextField("Address", vmc.sendHost);
                    vmc.sendPort = Ui.IntField("Port", vmc.sendPort, 1, 65535);
                    if (vmc.receiveEnabled && vmc.sendPort == vmc.receivePort && IsLocal(vmc.sendHost))
                        Ui.Hint("Sending to the port Mystic receives on would loop back into itself. Use another port.");
                }
            }

            Ui.Section("Hotkeys");
            Hotkeys.GlobalEnabled = Ui.Toggle("Work while in other apps", Hotkeys.GlobalEnabled);
            if (Hotkeys.GlobalEnabled)
            {
                Ui.Hint("Outside Mystic, hold the modifier too, so typing in chat doesn't trigger them:");
                GUILayout.BeginHorizontal();
                foreach (Hotkeys.Modifier m in System.Enum.GetValues(typeof(Hotkeys.Modifier)))
                {
                    if (Ui.Chip(Hotkeys.ModifierLabel(m), Hotkeys.GlobalModifier == m)) Hotkeys.GlobalModifier = m;
                    GUILayout.Space(4);
                }
                GUILayout.EndHorizontal();
            }
            string mod = Hotkeys.GlobalEnabled && Hotkeys.GlobalModifier != Hotkeys.Modifier.None
                ? Hotkeys.ModifierLabel(Hotkeys.GlobalModifier) + "+" : "";
            Ui.Hint($"1-5 moods, 0 clear, C calibrate, P pause, H panel, F2 stream mode, F3 debug." +
                    $"{(mod.Length > 0 ? $" In other apps: {mod}key, e.g. {mod}1." : "")}");

            if (window != null)
            {
                Ui.Section("Window");
                if (!NativeWindow.Supported)
                    Ui.Hint("Window size and always-on-top apply in the built app; inside the Unity editor they'd change the editor itself.");
                window.alwaysOnTop = Ui.Toggle("Always on top", window.alwaysOnTop);
                Ui.Gap(2);
                GUILayout.BeginHorizontal();
                if (Ui.Chip("1280x720", window.windowWidth == 1280 && window.windowHeight == 720)) window.SetSize(1280, 720);
                GUILayout.Space(4);
                if (Ui.Chip("1920x1080", window.windowWidth == 1920 && window.windowHeight == 1080)) window.SetSize(1920, 1080);
                GUILayout.EndHorizontal();
                Ui.Gap(4);
                GUILayout.BeginHorizontal();
                if (Ui.Chip("Vertical 720x1280", window.windowWidth == 720 && window.windowHeight == 1280)) window.SetSize(720, 1280);
                GUILayout.Space(4);
                if (Ui.Chip("Square 1080", window.windowWidth == 1080 && window.windowHeight == 1080)) window.SetSize(1080, 1080);
                GUILayout.EndHorizontal();
            }

            if (store != null)
            {
                Ui.Section("Settings");
                Ui.Hint($"Saved automatically. {store.Status}");
                Ui.Hint(SettingsStore.FilePath);
                if (Ui.Button("Reset all to defaults")) store.ResetToDefaults();
            }
        }
    }
}
