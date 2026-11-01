using System.Collections.Generic;
using UnityEngine;

namespace Mystic
{
    /// <summary>One decoded protocol-v1 packet, already converted to Unity space. Immutable once published.</summary>
    public sealed class TrackerFrame
    {
        public bool Valid;
        public int Seq;
        public double Time;
        public Vector3 HeadPos;
        public Quaternion HeadRot = Quaternion.identity;
        public readonly Dictionary<string, float> Blendshapes = new Dictionary<string, float>();

        // Optional body data (tracker started with --pose / --hands), already in Unity axes (see Parse).
        public Vector3[] Pose;       // 33 points, metres, hip-centred; null if not sent
        public float[] PoseVisibility;
        public Vector3[] HandLeft;   // 21 points of the user's own left hand, hand-centred; null if not seen
        public Vector3[] HandRight;

        public float Get(string name)
        {
            return Blendshapes.TryGetValue(name, out var v) ? v : 0f;
        }
    }
}
