using UniVRM10;
using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Arms, wrists and fingers from the tracker's optional body data (run it with --pose and/or --hands).
    /// Every bone is aimed so its direction matches yours, which works with any model's bone axes. Each side blends in
    /// when your arm/hand is visible and back to the rest pose (arms down, relaxed fingers) when it isn't.
    /// Runs after AvatarDriver so breathing has already moved the shoulders this frame.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ArmDriver : MonoBehaviour
    {
        public TrackerReceiver receiver;
        public AvatarLoader loader;
        public AvatarDriver driver;

        public bool trackArms = true;
        public bool trackHands = true;
        [Tooltip("How quickly the arms follow you. Lower = smoother, higher = snappier.")]
        [Range(2f, 30f)] public float follow = 12f;
        [Tooltip("Seconds over which an arm blends in or out when it appears or leaves the frame.")]
        public float blendSeconds = 0.25f;

        // MediaPipe hand landmark chains per finger (thumb, index, middle, ring, little): 4 points = 3 segments.
        static readonly int[][] FingerLandmarks =
        {
            new[] { 1, 2, 3, 4 }, new[] { 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 }, new[] { 13, 14, 15, 16 }, new[] { 17, 18, 19, 20 },
        };
        static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };
        static readonly string[] JointNames = { "Proximal", "Intermediate", "Distal" };

        sealed class Side
        {
            public Transform Upper, Lower, Hand;
            public Quaternion UpperRest, LowerRest, HandRest;
            public readonly Transform[,] Fingers = new Transform[5, 3];
            public int Shoulder, Elbow, Wrist;       // pose landmark indices
            public Vector3 UpperTarget, ForeTarget, UpperDir, ForeDir;
            public readonly Vector3[] LmTarget = new Vector3[21], Lm = new Vector3[21];
            public bool HasDirs, HasLm;
            public float ArmSeen = -10f, HandSeen = -10f, ArmW, HandW;
        }

        readonly Side _left = new Side { Shoulder = 11, Elbow = 13, Wrist = 15 };
        readonly Side _right = new Side { Shoulder = 12, Elbow = 14, Wrist = 16 };
        Vrm10Instance _vrm;
        int _lastSeq = -1;

        public bool PoseSeen => Time.time - Mathf.Max(_left.ArmSeen, _right.ArmSeen) < 0.5f;
        public bool LeftHandSeen => Time.time - _left.HandSeen < 0.5f;
        public bool RightHandSeen => Time.time - _right.HandSeen < 0.5f;

        void Start()
        {
            if (loader == null) return;
            // Subscribed after AvatarDriver, so its Bind (which lowers the arms) has run when we capture the rest pose.
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
            Bind(_left, vrm, "Left", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            Bind(_right, vrm, "Right", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
        }

        static void Bind(Side s, Vrm10Instance vrm, string side, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)
        {
            var hum = vrm.Humanoid;
            s.Upper = hum.GetBoneTransform(upper);
            s.Lower = hum.GetBoneTransform(lower);
            s.Hand = hum.GetBoneTransform(hand);
            if (s.Upper != null) s.UpperRest = s.Upper.localRotation;
            if (s.Lower != null) s.LowerRest = s.Lower.localRotation;
            if (s.Hand != null) s.HandRest = s.Hand.localRotation;
            for (int f = 0; f < 5; f++)
                for (int j = 0; j < 3; j++)
                    s.Fingers[f, j] = System.Enum.TryParse($"{side}{FingerNames[f]}{JointNames[j]}", out HumanBodyBones hb)
                        ? hum.GetBoneTransform(hb) : null;
            s.HasDirs = s.HasLm = false;
            s.ArmW = s.HandW = 0f;
        }

        void Update()
        {
            if (_vrm == null || receiver == null || driver == null) return;

            var frame = receiver.Latest;
            if (frame != null && frame.Seq != _lastSeq)
            {
                _lastSeq = frame.Seq;
                if (!driver.trackingPaused) Ingest(frame);
            }

            float dt = Time.deltaTime;
            var root = _vrm.transform.rotation;
            // Fingers start from the relaxed pose every frame; tracked hands are blended on top of it.
            if (trackHands && driver.HasFingers) driver.ApplyHandRelax();
            Process(_left, dt, root);
            Process(_right, dt, root);
        }

        void Ingest(TrackerFrame f)
        {
            if (f.Pose != null)
            {
                IngestArm(_left, f);
                IngestArm(_right, f);
            }
            IngestHand(_left, f.HandLeft);
            IngestHand(_right, f.HandRight);
        }

        static void IngestArm(Side s, TrackerFrame f)
        {
            if (f.Pose.Length <= s.Wrist) return;
            var vis = f.PoseVisibility;
            if (vis != null && Mathf.Min(vis[s.Shoulder], Mathf.Min(vis[s.Elbow], vis[s.Wrist])) < 0.5f) return;
            var upper = f.Pose[s.Elbow] - f.Pose[s.Shoulder];
            var fore = f.Pose[s.Wrist] - f.Pose[s.Elbow];
            if (upper.sqrMagnitude < 1e-6f || fore.sqrMagnitude < 1e-6f) return;
            s.UpperTarget = upper.normalized;
            s.ForeTarget = fore.normalized;
            s.ArmSeen = Time.time;
        }

        static void IngestHand(Side s, Vector3[] lm)
        {
            if (lm == null) return;
            System.Array.Copy(lm, s.LmTarget, 21);
            s.HandSeen = Time.time;
        }

        void Process(Side s, float dt, Quaternion root)
        {
            if (s.Upper == null || s.Lower == null || s.Hand == null) return;

            bool armOn = trackArms && Time.time - s.ArmSeen < 0.3f;
            bool handOn = trackHands && Time.time - s.HandSeen < 0.3f;
            float rate = dt / Mathf.Max(0.05f, blendSeconds);
            s.ArmW = Mathf.MoveTowards(s.ArmW, armOn ? 1f : 0f, rate);
            s.HandW = Mathf.MoveTowards(s.HandW, handOn ? 1f : 0f, rate);

            float k = 1f - Mathf.Exp(-follow * dt);
            if (armOn)
            {
                if (!s.HasDirs) { s.UpperDir = s.UpperTarget; s.ForeDir = s.ForeTarget; s.HasDirs = true; }
                s.UpperDir = Vector3.Slerp(s.UpperDir, s.UpperTarget, k);
                s.ForeDir = Vector3.Slerp(s.ForeDir, s.ForeTarget, k);
            }
            if (handOn)
            {
                if (!s.HasLm) { System.Array.Copy(s.LmTarget, s.Lm, 21); s.HasLm = true; }
                for (int i = 0; i < 21; i++) s.Lm[i] = Vector3.Lerp(s.Lm[i], s.LmTarget[i], k);
            }

            // Arms always restart from the rest pose (arms down), so nothing accumulates frame to frame.
            s.Upper.localRotation = s.UpperRest;
            s.Lower.localRotation = s.LowerRest;
            s.Hand.localRotation = s.HandRest;

            if (s.ArmW > 0.001f && s.HasDirs)
            {
                Aim(s.Upper, s.Lower.position, root * s.UpperDir);
                s.Upper.localRotation = Quaternion.Slerp(s.UpperRest, s.Upper.localRotation, s.ArmW);
                Aim(s.Lower, s.Hand.position, root * s.ForeDir);
                s.Lower.localRotation = Quaternion.Slerp(s.LowerRest, s.Lower.localRotation, s.ArmW);
            }

            // Wrist orientation only makes sense when the arm is tracked too; fingers work on their own.
            float wristW = s.HandW * s.ArmW;
            if (wristW > 0.001f && s.HasLm)
            {
                AimHand(s, root);
                s.Hand.localRotation = Quaternion.Slerp(s.HandRest, s.Hand.localRotation, wristW);
            }
            if (s.HandW > 0.001f && s.HasLm) AimFingers(s, root);
        }

        // Matches the hand's forward (wrist -> middle knuckle) and its side (little -> index knuckle).
        static void AimHand(Side s, Quaternion root)
        {
            var mid = s.Fingers[2, 0];
            var index = s.Fingers[1, 0];
            var little = s.Fingers[4, 0];
            var fwdT = root * (s.Lm[9] - s.Lm[0]);
            var sideT = root * (s.Lm[5] - s.Lm[17]);
            if (mid != null && index != null && little != null)
            {
                var fwdC = mid.position - s.Hand.position;
                var sideC = index.position - little.position;
                if (fwdC.sqrMagnitude < 1e-8f || fwdT.sqrMagnitude < 1e-8f) return;
                var current = Quaternion.LookRotation(fwdC, Vector3.Cross(fwdC, sideC));
                var target = Quaternion.LookRotation(fwdT, Vector3.Cross(fwdT, sideT));
                s.Hand.rotation = target * Quaternion.Inverse(current) * s.Hand.rotation;
            }
            else if (s.Hand.childCount > 0)
            {
                // No finger bones (e.g. Jinx): point the hand where yours points.
                Aim(s.Hand, s.Hand.GetChild(0).position, fwdT);
            }
        }

        // Each finger segment is aimed along the matching landmark segment, blended over the relaxed pose. Directions
        // are taken relative to the hand (landmark hand frame -> avatar hand frame), so fingers curl correctly even
        // when the wrist isn't tracked and the avatar's hand is still hanging at its side.
        static void AimFingers(Side s, Quaternion root)
        {
            var toAvatar = root;
            var mid = s.Fingers[2, 0];
            var index = s.Fingers[1, 0];
            var little = s.Fingers[4, 0];
            if (mid != null && index != null && little != null)
            {
                var fwdL = s.Lm[9] - s.Lm[0];
                var sideL = s.Lm[5] - s.Lm[17];
                var fwdC = mid.position - s.Hand.position;
                var sideC = index.position - little.position;
                if (fwdL.sqrMagnitude > 1e-8f && fwdC.sqrMagnitude > 1e-8f)
                {
                    var landmarkFrame = Quaternion.LookRotation(fwdL, Vector3.Cross(fwdL, sideL));
                    var handFrame = Quaternion.LookRotation(fwdC, Vector3.Cross(fwdC, sideC));
                    toAvatar = handFrame * Quaternion.Inverse(landmarkFrame);
                }
            }

            for (int f = 0; f < 5; f++)
            {
                var lm = FingerLandmarks[f];
                for (int j = 0; j < 3; j++)
                {
                    var t = s.Fingers[f, j];
                    if (t == null) continue;
                    var relaxed = t.localRotation;
                    var target = toAvatar * (s.Lm[lm[j + 1]] - s.Lm[lm[j]]);
                    var next = j < 2 ? s.Fingers[f, j + 1] : (t.childCount > 0 ? t.GetChild(0) : null);
                    if (next != null)
                    {
                        Aim(t, next.position, target);
                    }
                    else if (j > 0 && s.Fingers[f, j - 1] != null)
                    {
                        // Last joint without a tip bone: use the previous segment's direction as its own.
                        var current = t.position - s.Fingers[f, j - 1].position;
                        if (current.sqrMagnitude > 1e-10f && target.sqrMagnitude > 1e-10f)
                            t.rotation = Quaternion.FromToRotation(current, target) * t.rotation;
                    }
                    t.localRotation = Quaternion.Slerp(relaxed, t.localRotation, s.HandW);
                }
            }
        }

        static void Aim(Transform bone, Vector3 childPosition, Vector3 targetDir)
        {
            var current = childPosition - bone.position;
            if (current.sqrMagnitude < 1e-10f || targetDir.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(current, targetDir) * bone.rotation;
        }
    }
}
