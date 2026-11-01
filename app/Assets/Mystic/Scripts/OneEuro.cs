using UnityEngine;

namespace Mystic
{
    /// <summary>One Euro filter: heavy smoothing when still, low lag when moving fast.</summary>
    public sealed class OneEuroFilter
    {
        public float MinCutoff;
        public float Beta;
        float _x, _dx;
        bool _init;

        public OneEuroFilter(float minCutoff, float beta)
        {
            MinCutoff = minCutoff;
            Beta = beta;
        }

        public static float Alpha(float dt, float cutoff)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }

        public float Filter(float x, float dt)
        {
            if (!_init)
            {
                _init = true;
                _x = x;
                _dx = 0f;
                return x;
            }
            float dx = (x - _x) / dt;
            _dx += Alpha(dt, 1f) * (dx - _dx);
            float cutoff = MinCutoff + Beta * Mathf.Abs(_dx);
            _x += Alpha(dt, cutoff) * (x - _x);
            return _x;
        }
    }
}
