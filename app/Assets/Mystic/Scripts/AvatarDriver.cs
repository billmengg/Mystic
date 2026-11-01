using System.Collections.Generic;
using UniVRM10;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mystic
{
    /// <summary>Maps tracker frames onto a loaded VRM: head/neck rotation, blink, mouth, smile and eye gaze.</summary>
    public class AvatarDriver : MonoBehaviour
    {
        public TrackerReceiver receiver;
        public AvatarLoader loader;
        public LipSync lipSync;

        [Header("Head")]
        [Range(0f, 1f)] public float neckShare = 0.4f;
        [Tooltip("How much of the head rotation the upper body follows.")]
        [Range(0f, 1f)] public float torsoShare = 0.2f;
        [Tooltip("Rest pose: arms hang this many degrees away from straight down. Applied when the avatar loads.")]
        [Range(0f, 45f)] public float armOutDegrees = 12f;
        public float rotationGain = 1f;
        [Tooltip("Lower = smoother when still.")] public float headMinCutoff = 1.5f;
        [Tooltip("Higher = less lag when moving fast (cutoff gain per degree/second).")] public float headBeta = 0.04f;

        [Header("Face")]
        public float faceMinCutoff = 3f;
        public float faceBeta = 2f;
        public float gazeRangeDegrees = 30f;
        [Range(0f, 1f)] public float smileToHappy = 0.6f;

        [Header("Output")]
        [Tooltip("Render-rate follow speed; hides the tracker's frame stepping.")] public float renderFollow = 30f;

        Vrm10Instance _vrm;
        Transform _head, _neck, _spine, _chest, _upperChest;
        // Bone world rotations relative to the avatar root, captured at load.
        Quaternion _headInit, _neckInit, _spineInit, _chestInit, _upperChestInit;

        // Face calibration is separate from the head calibration on C, and opt-in. It records your relaxed face so
        // it can be subtracted from the blendshapes; later it becomes part of the face setup wizard.
        [Header("Face calibration (separate from C)")]
        [Tooltip("How long to average your relaxed face when you press Calibrate face.")]
        public float faceCalibrationSeconds = 1f;
        [Tooltip("Subtract the recorded resting face from every blendshape. Off until you calibrate the face.")]
        public bool useFaceBaseline = false;

        readonly Dictionary<string, float> _baseline = new Dictionary<string, float>();
        readonly Dictionary<string, float> _faceSum = new Dictionary<string, float>();
        int _faceCount;
        float _faceLeft;
        bool _faceCalibrating;
        public bool HasFaceBaseline => _baseline.Count > 0;

        int _lastSeq = -1;
        double _lastT = -1;
        bool _hasNeutral, _recalibrate;
        Quaternion _neutral = Quaternion.identity;
        Quaternion _headFiltered = Quaternion.identity;
        Quaternion _headShown = Quaternion.identity;

        readonly Dictionary<string, OneEuroFilter> _filters = new Dictionary<string, OneEuroFilter>();
        readonly Dictionary<string, float> _bs = new Dictionary<string, float>();
        float _yaw, _pitch;

        static readonly ExpressionKey KBlinkL = ExpressionKey.CreateFromPreset(ExpressionPreset.blinkLeft);
        static readonly ExpressionKey KBlinkR = ExpressionKey.CreateFromPreset(ExpressionPreset.blinkRight);
        static readonly ExpressionKey KAa = ExpressionKey.CreateFromPreset(ExpressionPreset.aa);
        static readonly ExpressionKey KIh = ExpressionKey.CreateFromPreset(ExpressionPreset.ih);
        static readonly ExpressionKey KOu = ExpressionKey.CreateFromPreset(ExpressionPreset.ou);
        static readonly ExpressionKey KOh = ExpressionKey.CreateFromPreset(ExpressionPreset.oh);
        static readonly ExpressionKey KEe = ExpressionKey.CreateFromPreset(ExpressionPreset.ee);
        static readonly ExpressionKey KHappy = ExpressionKey.CreateFromPreset(ExpressionPreset.happy);

        // Perfect Sync: models whose VRM has a custom expression per ARKit blendshape get the tracker's values
        // passed straight through, so brows, cheeks, eye direction etc. all move.
        static readonly HashSet<string> ArkitNames = new HashSet<string>
        {
            "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
            "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
            "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight",
            "eyeLookOutLeft", "eyeLookOutRight", "eyeLookUpLeft", "eyeLookUpRight",
            "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight",
            "jawForward", "jawLeft", "jawOpen", "jawRight",
            "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel",
            "mouthLeft", "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight",
            "mouthPucker", "mouthRight", "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper",
            "mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight",
            "mouthUpperUpLeft", "mouthUpperUpRight", "noseSneerLeft", "noseSneerRight", "tongueOut",
        };

        readonly Dictionary<string, ExpressionKey> _perfectSync = new Dictionary<string, ExpressionKey>();
        public bool PerfectSync { get; private set; }

        [Header("Perfect Sync")]
        [Tooltip("Scales the tracker's values on Perfect Sync models.")]
        [Range(0.5f, 2f)] public float faceIntensity = 1f;

        [Header("Tracking loss")]
        [Tooltip("Seconds without a face before the avatar counts as lost. It then holds its last pose, still breathing and blinking.")]
        public float lostGrace = 0.4f;
        [Tooltip("Freeze tracking (P). The avatar holds its pose, still breathing and blinking.")]
        public bool trackingPaused;
        float _lostFor;
        float _autoBlinkWait = 2f, _autoBlinkT = -1f;
        public bool TrackingLost => _lostFor > lostGrace;

        [Header("Hands")]
        [Tooltip("Curls the fingers into a relaxed rest pose (models with finger bones). 0 = straight.")]
        [Range(0f, 1.5f)] public float handRelax = 1f;
        float _appliedHandRelax = -1f;

        sealed class FingerBone
        {
            public Transform T;
            public Quaternion Rest;
            public Vector3 Axis; // bend axis in the bone's own space
            public float Angle;
        }

        readonly List<FingerBone> _fingers = new List<FingerBone>();
        public bool HasFingers => _fingers.Count > 0;

        // Relaxed curl per joint (proximal, intermediate, distal), in degrees.
        static readonly (string finger, float a, float b, float c)[] FingerCurl =
        {
            ("Thumb", 10f, 15f, 10f), ("Index", 15f, 25f, 20f), ("Middle", 20f, 30f, 20f),
            ("Ring", 25f, 35f, 25f), ("Little", 30f, 40f, 25f),
        };

        [Header("Idle")]
        [Tooltip("Breathing in the chest and shoulders.")]
        [Range(0f, 2f)] public float breathing = 1f;
        [Tooltip("Slow random sway of the upper body.")]
        [Range(0f, 2f)] public float sway = 1f;
        float _idleTime;
        Transform _lShoulder, _rShoulder;
        // Nothing else re-sets the shoulders each frame, so breathing must start from their rest pose every
        // frame; otherwise the small per-frame lift accumulates into a huge swing.
        Quaternion _lShoulderRest, _rShoulderRest;
        Vector3 _lShoulderRestPos, _rShoulderRestPos;
        [Tooltip("How far the shoulders rise at the top of a breath, in metres (times Breathing).")]
        public float shoulderLift = 0.004f;

        [Header("Lean (head position)")]
        [Tooltip("How much moving your head toward the camera or sideways moves the avatar.")]
        [Range(0f, 1.5f)] public float leanAmount = 0.6f;
        [Tooltip("Furthest the avatar may shift, in metres.")]
        public float leanLimit = 0.12f;
        Vector3 _neutralPos, _posFiltered, _leanShown, _rootBase;
        bool _rootBaseSet;
        OneEuroFilter[] _posFilters = NewPosFilters();
        static OneEuroFilter[] NewPosFilters() =>
            new[] { new OneEuroFilter(1f, 2f), new OneEuroFilter(1f, 2f), new OneEuroFilter(1f, 2f) };

        [Header("Blink & mouth")]
        [Tooltip("Tracker blink value below which the eye is fully open.")]
        [Range(0f, 0.5f)] public float blinkOpenBelow = 0.15f;
        [Tooltip("Tracker blink value at which the eye is fully closed.")]
        [Range(0.3f, 1f)] public float blinkClosedAt = 0.7f;
        [Tooltip("Tracker jaw value at which the mouth starts to open.")]
        [Range(0f, 0.3f)] public float mouthOpenFrom = 0.05f;
        [Tooltip("Tracker jaw value at which the mouth is fully open.")]
        [Range(0.2f, 1f)] public float mouthFullAt = 0.6f;

        [Header("Motion gate")]
        [Tooltip("Max head turn speed (deg/s). Faster motion is followed at this speed instead of snapping.")]
        public float maxHeadSpeed = 360f;
        [Tooltip("Max increase in turn speed per second (deg/s^2). Stops sudden jolts that make hair twitch.")]
        public float maxHeadAccel = 2400f;
        [Tooltip("A tracker frame implying a turn faster than this (deg/s) is treated as a glitch and dropped.")]
        public float glitchSpeed = 900f;
        public int GlitchesIgnored { get; private set; }
        /// <summary>Head turn speed implied by the latest tracker frame (deg/s), before any gating.</summary>
        public float LastRawHeadSpeed { get; private set; }
        /// <summary>Head turn speed the avatar is actually showing (deg/s), after the motion gate.</summary>
        public float HeadShownSpeed => _headSpeed;
        Quaternion _lastRel = Quaternion.identity;
        int _glitchRun;
        float _headSpeed;

        // Spring-bone physics (hair, clothes). Tuned live on top of the values authored in the VRM.
        [Header("Spring bone physics")]
        [Tooltip("Multiplies the authored stiffness. Lower = floppier.")]
        [Range(0.05f, 2f)] public float hairStiffness = 1f;
        [Tooltip("Added to the authored gravity. Higher = hangs lower.")]
        [Range(0f, 2f)] public float hairGravity = 0f;

        sealed class JointBase
        {
            public VRM10SpringBoneJoint Joint;
            public float Stiffness, Gravity;
        }

        [Tooltip("Hair strands collide with each other (only on models built with strand colliders).")]
        public bool strandCollision = true;
        public bool HasStrandColliders { get; private set; }
        bool _appliedStrandCollision = true;
        readonly List<(Vrm10InstanceSpringBone.Spring spring, List<VRM10SpringBoneColliderGroup> all,
            List<VRM10SpringBoneColliderGroup> bodyOnly)> _springGroups =
            new List<(Vrm10InstanceSpringBone.Spring, List<VRM10SpringBoneColliderGroup>, List<VRM10SpringBoneColliderGroup>)>();

        readonly List<JointBase> _springJoints = new List<JointBase>();
        float _appliedStiffness = 1f, _appliedGravity;
        public bool HasSprings => _springJoints.Count > 0;

        public enum Mood { None, Happy, Angry, Sad, Surprised, Relaxed }

        // Indexed by (int)Mood; slot 0 is unused.
        static readonly ExpressionKey[] KMood =
        {
            ExpressionKey.CreateFromPreset(ExpressionPreset.neutral),
            KHappy,
            ExpressionKey.CreateFromPreset(ExpressionPreset.angry),
            ExpressionKey.CreateFromPreset(ExpressionPreset.sad),
            ExpressionKey.CreateFromPreset(ExpressionPreset.surprised),
            ExpressionKey.CreateFromPreset(ExpressionPreset.relaxed),
        };

        [Header("Mood")]
        [Tooltip("Keys 1-5 toggle Happy, Angry, Sad, Surprised, Relaxed; 0 clears. Higher = faster blend.")]
        public float moodFadeSpeed = 8f;
        public Mood CurrentMood { get; private set; }
        readonly float[] _moodWeight = new float[6];

        public void ToggleMood(Mood m) => CurrentMood = CurrentMood == m ? Mood.None : m;

        void OnEnable()
        {
            if (loader != null)
            {
                loader.Loaded += Bind;
                if (loader.Avatar != null) Bind(loader.Avatar);
            }
        }

        void OnDisable()
        {
            if (loader != null) loader.Loaded -= Bind;
        }

        // Every humanoid bone's T-pose (as loaded, before the arms are lowered), relative to the avatar root. VMC
        // describes bones as rotations away from a standard T-pose, so converting needs the model's own T-pose.
        readonly Dictionary<HumanBodyBones, (Quaternion rot, Vector3 pos)> _tpose =
            new Dictionary<HumanBodyBones, (Quaternion, Vector3)>();

        public bool TryGetTPose(HumanBodyBones bone, out Quaternion rotation, out Vector3 position)
        {
            if (_tpose.TryGetValue(bone, out var t)) { rotation = t.rot; position = t.pos; return true; }
            rotation = Quaternion.identity;
            position = Vector3.zero;
            return false;
        }

        void CaptureTPose(Vrm10Instance vrm)
        {
            _tpose.Clear();
            var root = vrm.transform;
            var inv = Quaternion.Inverse(root.rotation);
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = vrm.Humanoid.GetBoneTransform((HumanBodyBones)i);
                if (t != null) _tpose[(HumanBodyBones)i] = (inv * t.rotation, root.InverseTransformPoint(t.position));
            }
        }

        void Bind(Vrm10Instance vrm)
        {
            CaptureTPose(vrm); // first, before anything below changes the pose
            _vrm = vrm;
            _head = vrm.Humanoid.Head;
            _neck = vrm.Humanoid.Neck;
            _lShoulder = vrm.Humanoid.LeftShoulder;
            _rShoulder = vrm.Humanoid.RightShoulder;
            if (_lShoulder != null) { _lShoulderRest = _lShoulder.localRotation; _lShoulderRestPos = _lShoulder.localPosition; }
            if (_rShoulder != null) { _rShoulderRest = _rShoulder.localRotation; _rShoulderRestPos = _rShoulder.localPosition; }
            _rootBaseSet = false;
            _leanShown = Vector3.zero;
            _spine = vrm.Humanoid.Spine;
            _chest = vrm.Humanoid.Chest;
            _upperChest = vrm.Humanoid.UpperChest;

            // Models import in a T-pose; drop the arms before capturing anything else.
            CollectFingers(vrm); // must run in the T-pose, before the arms are lowered
            LowerArm(vrm.Humanoid.LeftUpperArm, vrm.Humanoid.LeftLowerArm, -1f);
            LowerArm(vrm.Humanoid.RightUpperArm, vrm.Humanoid.RightLowerArm, 1f);

            var inv = Quaternion.Inverse(vrm.transform.rotation);
            if (_head != null) _headInit = inv * _head.rotation;
            if (_neck != null) _neckInit = inv * _neck.rotation;
            if (_spine != null) _spineInit = inv * _spine.rotation;
            if (_chest != null) _chestInit = inv * _chest.rotation;
            if (_upperChest != null) _upperChestInit = inv * _upperChest.rotation;
            _hasNeutral = false;
            CollectSpringJoints(vrm);

            _perfectSync.Clear();
            foreach (var key in vrm.Runtime.Expression.ExpressionKeys)
                if (key.Preset == ExpressionPreset.custom && ArkitNames.Contains(key.Name))
                    _perfectSync[key.Name] = key;
            PerfectSync = _perfectSync.Count >= 30;
            Debug.Log($"[Mystic] Perfect Sync expressions found: {_perfectSync.Count} ({(PerfectSync ? "enabled" : "disabled")})");
        }

        void CollectSpringJoints(Vrm10Instance vrm)
        {
            _springJoints.Clear();
            _springGroups.Clear();
            HasStrandColliders = false;
            foreach (var spring in vrm.SpringBone.Springs)
            {
                foreach (var joint in spring.Joints)
                    if (joint != null)
                        _springJoints.Add(new JointBase { Joint = joint, Stiffness = joint.m_stiffnessForce, Gravity = joint.m_gravityPower });

                // Hair-on-hair colliders are the groups named "strand ..." (made by the Convert60 build script).
                var all = new List<VRM10SpringBoneColliderGroup>(spring.ColliderGroups);
                var bodyOnly = new List<VRM10SpringBoneColliderGroup>();
                foreach (var g in all)
                    if (g == null || g.Name == null || !g.Name.StartsWith("strand")) bodyOnly.Add(g);
                if (bodyOnly.Count != all.Count) HasStrandColliders = true;
                _springGroups.Add((spring, all, bodyOnly));
            }
            _appliedStrandCollision = true; // as authored
            // A new model starts at its authored values; the sliders re-apply on the next frame if they differ.
            _appliedStiffness = 1f;
            _appliedGravity = 0f;
            Debug.Log($"[Mystic] Spring bone joints found: {_springJoints.Count}");
        }

        void ApplyStrandCollision()
        {
            _appliedStrandCollision = strandCollision;
            foreach (var (spring, all, bodyOnly) in _springGroups)
                spring.ColliderGroups = new List<VRM10SpringBoneColliderGroup>(strandCollision ? all : bodyOnly);
            try
            {
                _vrm.Runtime.SpringBone.ReconstructSpringBone();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Mystic] Could not rebuild spring bones: {e.Message}");
            }
            Debug.Log($"[Mystic] Hair-on-hair collision {(strandCollision ? "on" : "off")}");
        }

        void ApplySpringSettings()
        {
            _appliedStiffness = hairStiffness;
            _appliedGravity = hairGravity;
            try
            {
                foreach (var h in _springJoints)
                {
                    if (h.Joint == null) continue;
                    h.Joint.m_stiffnessForce = h.Stiffness * hairStiffness;
                    h.Joint.m_gravityPower = h.Gravity + hairGravity;
                    _vrm.Runtime.SpringBone.SetJointLevel(h.Joint.transform, h.Joint.Blittable);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Mystic] Could not update spring bone settings: {e.Message}");
            }
        }

        // side: -1 for the avatar's left arm (-X), +1 for its right (+X).
        void LowerArm(Transform upper, Transform lower, float side)
        {
            if (upper == null || lower == null) return;
            var dir = (lower.position - upper.position).normalized;
            float a = armOutDegrees * Mathf.Deg2Rad;
            var outward = _vrm.transform.right * side;
            var target = (Vector3.down * Mathf.Cos(a) + outward * Mathf.Sin(a)).normalized;
            upper.rotation = Quaternion.FromToRotation(dir, target) * upper.rotation;
        }

        static void Follow(Transform bone, Quaternion init, Quaternion root, Quaternion rel, float share)
        {
            if (bone != null)
                bone.rotation = root * Quaternion.Slerp(Quaternion.identity, rel, share) * init;
        }

        // Manual "Reset hair" only. Long frames can't fling the hair any more: Mystic keeps running in the
        // background and Time.maximumDeltaTime caps each physics step at 50 ms (see MysticBootstrap). An automatic
        // reset on refocus made the hair visibly snap to its rest pose and bounce.
        bool _resetSprings;
        public void ResetSprings() => _resetSprings = true;

        void Update()
        {
            if (_resetSprings && _vrm != null && HasSprings)
            {
                _resetSprings = false;
                try { _vrm.Runtime.SpringBone.RestoreInitialTransform(); }
                catch (System.Exception e) { Debug.LogWarning($"[Mystic] Could not reset spring bones: {e.Message}"); }
            }
            if (_vrm == null || receiver == null) return;

            if (Hotkeys.Pressed(Key.C)) _recalibrate = true;
            if (Hotkeys.Pressed(Key.P)) trackingPaused = !trackingPaused;
            if (Hotkeys.Pressed(Key.Digit1)) ToggleMood(Mood.Happy);
            if (Hotkeys.Pressed(Key.Digit2)) ToggleMood(Mood.Angry);
            if (Hotkeys.Pressed(Key.Digit3)) ToggleMood(Mood.Sad);
            if (Hotkeys.Pressed(Key.Digit4)) ToggleMood(Mood.Surprised);
            if (Hotkeys.Pressed(Key.Digit5)) ToggleMood(Mood.Relaxed);
            if (Hotkeys.Pressed(Key.Digit0)) CurrentMood = Mood.None;
            if (HasFingers && !Mathf.Approximately(handRelax, _appliedHandRelax)) ApplyHandRelax();

            var frame = receiver.Latest;
            if (frame != null && frame.Seq != _lastSeq)
            {
                _lastSeq = frame.Seq;
                if (!trackingPaused) OnFrame(frame);
            }

            if (HasStrandColliders && strandCollision != _appliedStrandCollision)
                ApplyStrandCollision();
            if (HasSprings && (!Mathf.Approximately(hairStiffness, _appliedStiffness) || !Mathf.Approximately(hairGravity, _appliedGravity)))
                ApplySpringSettings();

            _lostFor = State == TrackerState.Tracking && !trackingPaused ? 0f : _lostFor + Time.deltaTime;
            Apply(Time.deltaTime);
        }

        // Called in the T-pose (before the arms are lowered). VRM T-poses have palms facing down, so "toward the palm"
        // is world down. Each bend axis is stored in the bone's own space, so it stays correct after the arms move.
        void CollectFingers(Vrm10Instance vrm)
        {
            _fingers.Clear();
            _appliedHandRelax = -1f;
            string[] joints = { "Proximal", "Intermediate", "Distal" };
            foreach (var side in new[] { "Left", "Right" })
            {
                foreach (var (finger, a, b, c) in FingerCurl)
                {
                    var bones = new Transform[3];
                    for (int i = 0; i < 3; i++)
                        if (System.Enum.TryParse($"{side}{finger}{joints[i]}", out HumanBodyBones hb))
                            bones[i] = vrm.Humanoid.GetBoneTransform(hb);
                    float[] angles = { a, b, c };
                    for (int i = 0; i < 3; i++)
                    {
                        var t = bones[i];
                        if (t == null) continue;
                        // Direction of this segment: toward the next joint, or for the last one, its child or the
                        // previous segment's direction.
                        Transform next = i < 2 ? bones[i + 1] : (t.childCount > 0 ? t.GetChild(0) : null);
                        Vector3 dir = next != null ? next.position - t.position
                            : i > 0 && bones[i - 1] != null ? t.position - bones[i - 1].position : Vector3.zero;
                        var axis = Vector3.Cross(dir.normalized, Vector3.down); // rotating about this bends toward down
                        if (axis.sqrMagnitude < 1e-6f) continue;
                        _fingers.Add(new FingerBone
                        {
                            T = t, Rest = t.localRotation, Axis = t.InverseTransformDirection(axis.normalized), Angle = angles[i],
                        });
                    }
                }
            }
        }

        public void ApplyHandRelax()
        {
            _appliedHandRelax = handRelax;
            foreach (var f in _fingers)
                if (f.T != null) f.T.localRotation = f.Rest * Quaternion.AngleAxis(f.Angle * handRelax, f.Axis);
        }

        void OnFrame(TrackerFrame f)
        {
            if (!f.Valid) return;

            float dt = _lastT < 0 ? 1f / 30f : Mathf.Clamp((float)(f.Time - _lastT), 0.001f, 0.1f);
            _lastT = f.Time;

            // Head: rotation relative to the calibrated neutral pose, in tracker axes.
            if (_recalibrate || !_hasNeutral)
            {
                _neutral = f.HeadRot;
                _hasNeutral = true;
                _recalibrate = false;
                _headFiltered = Quaternion.identity;
                _lastRel = Quaternion.identity;
                _glitchRun = 0;
                _neutralPos = f.HeadPos;
                _posFiltered = Vector3.zero;
                _posFilters = NewPosFilters();
            }
            var rel = f.HeadRot * Quaternion.Inverse(_neutral);

            // Glitch gate: a frame implying an impossibly fast turn is dropped. If it persists for several frames it
            // is real motion (or a big jump like recalibration) and gets through.
            float rawSpeed = AngleDeg(_lastRel, rel) / dt;
            LastRawHeadSpeed = rawSpeed;
            if (rawSpeed > glitchSpeed && _glitchRun < 3)
            {
                _glitchRun++;
                GlitchesIgnored++;
            }
            else
            {
                _glitchRun = 0;
                _lastRel = rel;
                float speed = AngleDeg(_headFiltered, rel) / dt; // degrees per second
                float cutoff = headMinCutoff + headBeta * speed;
                _headFiltered = Quaternion.Slerp(_headFiltered, rel, OneEuroFilter.Alpha(dt, cutoff));
            }

            // Head position relative to where you were at calibration, smoothed. Drives the lean.
            var delta = f.HeadPos - _neutralPos;
            _posFiltered = new Vector3(_posFilters[0].Filter(delta.x, dt), _posFilters[1].Filter(delta.y, dt),
                _posFilters[2].Filter(delta.z, dt));

            if (_faceCalibrating)
            {
                foreach (var kv in f.Blendshapes)
                    _faceSum[kv.Key] = (_faceSum.TryGetValue(kv.Key, out var s) ? s : 0f) + kv.Value;
                _faceCount++;
                _faceLeft -= dt;
                if (_faceLeft <= 0f) FinishFaceCalibration();
            }

            // Face: smooth every blendshape independently.
            foreach (var kv in f.Blendshapes)
            {
                if (!_filters.TryGetValue(kv.Key, out var filter))
                {
                    filter = new OneEuroFilter(faceMinCutoff, faceBeta);
                    _filters[kv.Key] = filter;
                }
                filter.MinCutoff = faceMinCutoff; // picks up live changes from the settings panel
                filter.Beta = faceBeta;
                _bs[kv.Key] = filter.Filter(kv.Value, dt);
            }
        }

        /// <summary>Starts recording your relaxed face. Does not touch the head calibration.</summary>
        public void CalibrateFace()
        {
            _faceCalibrating = true;
            _faceLeft = faceCalibrationSeconds;
            _faceCount = 0;
            _faceSum.Clear();
        }

        public void ClearFaceCalibration()
        {
            _faceCalibrating = false;
            _baseline.Clear();
            useFaceBaseline = false;
        }

        void FinishFaceCalibration()
        {
            _faceCalibrating = false;
            _baseline.Clear();
            foreach (var kv in _faceSum) _baseline[kv.Key] = kv.Value / _faceCount;
            useFaceBaseline = true;
            Debug.Log($"[Mystic] Face calibrated from {_faceCount} frames. Resting values: " +
                      $"jawOpen={Resting("jawOpen"):0.00} browOuterUpL={Resting("browOuterUpLeft"):0.00} " +
                      $"eyeSquintL={Resting("eyeSquintLeft"):0.00}");
        }

        float Resting(string name) => _baseline.TryGetValue(name, out var v) ? v : 0f;

        void Apply(float dt)
        {
            // Motion gate: follow the filtered head, but never faster than maxHeadSpeed and never speeding up faster
            // than maxHeadAccel. Spring bones react to acceleration, so this is what keeps hair from twitching.
            var headTarget = Quaternion.Slerp(_headShown, _headFiltered, 1f - Mathf.Exp(-renderFollow * dt));
            float want = AngleDeg(_headShown, headTarget);
            float speedCap = Mathf.Min(maxHeadSpeed, _headSpeed + maxHeadAccel * dt);
            float step = Mathf.Min(want, speedCap * dt);
            _headShown = want > 1e-6f ? Quaternion.SlerpUnclamped(_headShown, headTarget, step / want) : headTarget;
            _headSpeed = dt > 0f ? step / dt : 0f;
            var rel = Quaternion.SlerpUnclamped(Quaternion.identity, _headShown, rotationGain);
            var root = _vrm.transform.rotation;
            // Top-down so each bone's absolute rotation is the last write; shares grow toward the head.
            Follow(_spine, _spineInit, root, rel, torsoShare * 0.4f);
            Follow(_chest, _chestInit, root, rel, torsoShare * 0.7f);
            Follow(_upperChest, _upperChestInit, root, rel, torsoShare);
            Follow(_neck, _neckInit, root, rel, Mathf.Max(neckShare, torsoShare));
            Follow(_head, _headInit, root, rel, 1f);
            ApplyIdle(dt);
            ApplyLean(dt);
            // While lost or paused the pose holds, but the eyes keep blinking on their own instead of freezing.
            bool lost = TrackingLost;
            float autoBlink = lost ? AutoBlink(dt) : 0f;

            // Moods fade in and out; they work even before the tracker is running.
            for (int i = 1; i < _moodWeight.Length; i++)
            {
                float target = (int)CurrentMood == i ? 1f : 0f;
                _moodWeight[i] = Mathf.MoveTowards(_moodWeight[i], target, moodFadeSpeed * dt);
                if (i != (int)Mood.Happy) _vrm.Runtime.Expression.SetWeight(KMood[i], _moodWeight[i]);
            }

            if (_bs.Count == 0)
            {
                _vrm.Runtime.Expression.SetWeight(KHappy, _moodWeight[(int)Mood.Happy]);
                SetBlink(_vrm.Runtime.Expression, autoBlink, autoBlink);
                SetMicVisemes(_vrm.Runtime.Expression, MicActive ? 1f : 0f);
                return;
            }

            // Eye gaze. ARKit "Out" is toward the ear; the subject's left gaze is
            // (outLeft + inRight)/2 - (inLeft + outRight)/2, and the avatar mirrors it onto its own left (negative yaw).
            float left = (B("eyeLookOutLeft") + B("eyeLookInRight")) * 0.5f;
            float right = (B("eyeLookInLeft") + B("eyeLookOutRight")) * 0.5f;
            float up = (B("eyeLookUpLeft") + B("eyeLookUpRight")) * 0.5f;
            float down = (B("eyeLookDownLeft") + B("eyeLookDownRight")) * 0.5f;
            float targetYaw = (right - left) * gazeRangeDegrees;
            float targetPitch = (up - down) * gazeRangeDegrees;
            float follow = 1f - Mathf.Exp(-renderFollow * dt);
            _yaw = Mathf.Lerp(_yaw, targetYaw, follow);
            _pitch = Mathf.Lerp(_pitch, targetPitch, follow);
            _vrm.Runtime.LookAt.SetYawPitchManually(_yaw, _pitch);

            var ex = _vrm.Runtime.Expression;

            // Camera mouth is dropped when the mic should drive it alone: mic-only mode, or the face is lost (a held
            // mouth would otherwise freeze open while you keep talking).
            float camMouth = MicActive && (lipSync.mode == LipSync.Mode.MicOnly || lost) ? 0f : 1f;

            if (PerfectSync)
            {
                foreach (var kv in _perfectSync)
                {
                    float v = B(kv.Key);
                    if (camMouth == 0f && MouthShapes.Contains(kv.Key)) v = 0f;
                    // MediaPipe's blink scores rarely reach 1, so stretch them to a full close.
                    if (kv.Key.StartsWith("eyeBlink")) v = lost ? autoBlink : Remap(v, blinkOpenBelow, blinkClosedAt);
                    else if (kv.Key == "jawOpen") v = Remap(v, mouthOpenFrom, mouthFullAt);
                    ex.SetWeight(kv.Value, Mathf.Clamp01(v * faceIntensity));
                }
                ex.SetWeight(KHappy, _moodWeight[(int)Mood.Happy]); // smile comes through mouthSmile*; Happy is mood only
                SetMicVisemes(ex, MicFill(Remap(B("jawOpen"), mouthOpenFrom, mouthFullAt) * camMouth));
                return;
            }

            SetBlink(ex, lost ? autoBlink : Remap(B("eyeBlinkLeft"), blinkOpenBelow, blinkClosedAt),
                lost ? autoBlink : Remap(B("eyeBlinkRight"), blinkOpenBelow, blinkClosedAt));

            float smile = (B("mouthSmileLeft") + B("mouthSmileRight")) * 0.5f;
            float stretch = (B("mouthStretchLeft") + B("mouthStretchRight")) * 0.5f;
            float camAa = Remap(B("jawOpen"), mouthOpenFrom, mouthFullAt) * camMouth;
            float fill = MicFill(camAa);
            ex.SetWeight(KAa, Mathf.Clamp01(camAa + Mic(0) * fill));
            ex.SetWeight(KIh, Mic(1) * fill);
            ex.SetWeight(KOu, Mathf.Clamp01(Remap(B("mouthPucker"), 0.1f, 0.7f) * camMouth + Mic(2) * fill));
            ex.SetWeight(KOh, Mathf.Clamp01(Remap(B("mouthFunnel"), 0.1f, 0.7f) * 0.7f * camMouth + Mic(4) * fill));
            ex.SetWeight(KEe, Mathf.Clamp01(Remap(stretch, 0.1f, 0.6f) * camMouth + Mic(3) * fill));
            // Your own smile and the Happy mood share one expression, so take whichever is stronger.
            ex.SetWeight(KHappy, Mathf.Max(Remap(smile, 0.2f, 0.8f) * smileToHappy, _moodWeight[(int)Mood.Happy]));
        }

        // Exact angle between two rotations in degrees. Unity's Quaternion.Angle (and RotateTowards, which uses it)
        // returns 0 below ~0.16 degrees, which at high frame rates is most frames and broke the motion gate.
        static float AngleDeg(Quaternion a, Quaternion b)
        {
            var d = Quaternion.Inverse(a) * b;
            float v = Mathf.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            return 2f * Mathf.Atan2(v, Mathf.Abs(d.w)) * Mathf.Rad2Deg;
        }

        // A tracked blendshape value, with the resting face subtracted if face calibration is on.
        float B(string name)
        {
            if (!_bs.TryGetValue(name, out var v)) return 0f;
            if (useFaceBaseline && _baseline.TryGetValue(name, out var b))
                v = Mathf.Clamp01((v - b) / Mathf.Max(0.1f, 1f - b));
            return v;
        }

        // ARKit shapes that form the mouth opening; dropped when the mic drives the mouth alone. Smile, frown and
        // the like stay with the camera, so expressions still come through while talking.
        static readonly HashSet<string> MouthShapes = new HashSet<string>
        {
            "jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthLowerDownLeft", "mouthLowerDownRight",
            "mouthUpperUpLeft", "mouthUpperUpRight", "mouthStretchLeft", "mouthStretchRight",
        };

        bool MicActive => lipSync != null && lipSync.Active;

        float Mic(int viseme) => MicActive ? lipSync.Visemes[viseme] : 0f;

        // How much the mic may add on top of the camera mouth: in blend mode it fills in what the camera doesn't
        // show (a covered or lost mouth), so the two never double up when both see you talking.
        float MicFill(float cameraOpen)
        {
            if (!MicActive) return 0f;
            return lipSync.mode == LipSync.Mode.MicOnly ? 1f : Mathf.Clamp01(1f - cameraOpen);
        }

        void SetMicVisemes(Vrm10RuntimeExpression ex, float fill)
        {
            ex.SetWeight(KAa, Mic(0) * fill);
            ex.SetWeight(KIh, Mic(1) * fill);
            ex.SetWeight(KOu, Mic(2) * fill);
            ex.SetWeight(KEe, Mic(3) * fill);
            ex.SetWeight(KOh, Mic(4) * fill);
        }

        void SetBlink(Vrm10RuntimeExpression ex, float left, float right)
        {
            if (PerfectSync && _perfectSync.TryGetValue("eyeBlinkLeft", out var kl)
                && _perfectSync.TryGetValue("eyeBlinkRight", out var kr))
            {
                ex.SetWeight(kl, left);
                ex.SetWeight(kr, right);
            }
            else
            {
                ex.SetWeight(KBlinkL, left);
                ex.SetWeight(KBlinkR, right);
            }
        }

        // Natural blinking while idle: a 0.16 s blink every 2.5-6 s. Returns the blink weight this frame.
        float AutoBlink(float dt)
        {
            const float duration = 0.16f;
            if (_autoBlinkT >= 0f)
            {
                _autoBlinkT += dt;
                if (_autoBlinkT >= duration) { _autoBlinkT = -1f; _autoBlinkWait = Random.Range(2.5f, 6f); return 0f; }
                return Mathf.Sin(_autoBlinkT / duration * Mathf.PI);
            }
            _autoBlinkWait -= dt;
            if (_autoBlinkWait <= 0f) _autoBlinkT = 0f;
            return 0f;
        }

        // Breathing (chest + shoulders) and a slow random sway (spine). Applied after the head follow, so they
        // ride on top of tracking; small enough not to fight it.
        void ApplyIdle(float dt)
        {
            _idleTime += dt;
            var t = _vrm.transform;
            float swayRoll = (Mathf.PerlinNoise(_idleTime * 0.15f, 0.3f) - 0.5f) * 2.4f * sway;
            float swayYaw = (Mathf.PerlinNoise(0.7f, _idleTime * 0.12f) - 0.5f) * 2.4f * sway;
            if (_spine != null)
                _spine.rotation = Quaternion.AngleAxis(swayRoll, t.forward) * Quaternion.AngleAxis(swayYaw, t.up) * _spine.rotation;

            float breath = Mathf.Sin(_idleTime * 2f * Mathf.PI / 4.2f) * breathing; // ~14 breaths a minute
            var chestBone = _upperChest != null ? _upperChest : _chest;
            if (chestBone != null) chestBone.rotation = Quaternion.AngleAxis(-0.6f * breath, t.right) * chestBone.rotation;
            // Shoulders rise on the in-breath: a small upward lift plus a slight outward tilt. Rest pose is the
            // out-breath, so they never sink below it. +angle about forward raises the right side; left is negated.
            float inhale = (Mathf.Sin(_idleTime * 2f * Mathf.PI / 4.2f) * 0.5f + 0.5f) * breathing;
            BreatheShoulder(_rShoulder, _rShoulderRest, _rShoulderRestPos, 1f, inhale, t);
            BreatheShoulder(_lShoulder, _lShoulderRest, _lShoulderRestPos, -1f, inhale, t);
        }

        void BreatheShoulder(Transform shoulder, Quaternion restRot, Vector3 restPos, float side, float inhale, Transform t)
        {
            if (shoulder == null) return;
            shoulder.localRotation = restRot;
            shoulder.localPosition = restPos;
            if (shoulder.parent != null)
                shoulder.localPosition = restPos + shoulder.parent.InverseTransformVector(t.up * (shoulderLift * inhale));
            shoulder.rotation = Quaternion.AngleAxis(side * 1.0f * inhale, t.forward) * shoulder.rotation;
        }

        // Moves the whole avatar with your head position (lean in / sideways), smoothed and limited.
        void ApplyLean(float dt)
        {
            if (!_rootBaseSet) { _rootBase = _vrm.transform.localPosition; _rootBaseSet = true; }
            var target = Vector3.ClampMagnitude(_posFiltered * leanAmount, leanLimit);
            _leanShown = Vector3.Lerp(_leanShown, target, 1f - Mathf.Exp(-renderFollow * dt));
            _vrm.transform.localPosition = _rootBase + _leanShown;
        }

        static float Remap(float v, float lo, float hi) => Mathf.Clamp01((v - lo) / (hi - lo));

        public void Calibrate() => _recalibrate = true;

        public enum TrackerState { NoData, NoFace, Tracking }

        public TrackerState State
        {
            get
            {
                if (receiver == null || receiver.SecondsSincePacket > 1.0) return TrackerState.NoData;
                if (receiver.Latest != null && !receiver.Latest.Valid) return TrackerState.NoFace;
                return TrackerState.Tracking;
            }
        }

        public string Status
        {
            get
            {
                string status;
                if (receiver == null || receiver.SecondsSincePacket > 1.0) status = "Tracker: no data (is it running?)";
                else if (receiver.Latest != null && !receiver.Latest.Valid) status = "Tracker: no face in view";
                else status = "Tracker: OK";
                if (_faceCalibrating) status += "\nCalibrating face: hold a relaxed expression";
                if (trackingPaused) status += "\nTracking paused (P to resume)";
                else if (TrackingLost) status += "\nTracking lost: holding pose";
                if (_vrm == null) status += "\nNo avatar loaded";
                return status;
            }
        }
    }
}
