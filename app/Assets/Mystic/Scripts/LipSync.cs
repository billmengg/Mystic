using UnityEngine;

namespace Mystic
{
    /// <summary>
    /// Microphone lip sync. Loudness opens the mouth; the two lowest voice resonances (formants F1/F2) pick the vowel
    /// shape (A, I, U, E, O). Nothing is played back, so there is no echo. Off until you switch it on.
    /// The vowel estimate is an approximation (spectral centroids, not a trained model); turn vowels off for plain
    /// open/close.
    /// </summary>
    public class LipSync : MonoBehaviour
    {
        public enum Mode { BlendWithCamera, MicOnly }

        public bool micEnabled;
        public string device;   // null/empty = system default
        public Mode mode = Mode.BlendWithCamera;
        [Tooltip("Sound quieter than this (dB) keeps the mouth closed. Raise it if background noise moves the mouth.")]
        public float gateDb = -45f;
        [Tooltip("How many dB above the gate counts as a fully open mouth.")]
        public float rangeDb = 25f;
        public bool vowels = true;

        // aa, ih, ou, ee, oh: the VRM vowel expressions.
        public readonly float[] Visemes = new float[5];
        public float Level { get; private set; }     // 0..1 loudness after the gate, for the meter
        public float Open { get; private set; }      // smoothed mouth openness
        public bool Active => micEnabled && _clip != null;
        public string ActiveDevice => _activeDevice;
        public string Error { get; private set; }

        const int N = 1024; // FFT window (64 ms at 16 kHz)
        const float Attack = 0.03f, Release = 0.10f;

        // Vowel targets in this estimator's own output space (band centroids, not true formants: centroids are pulled
        // toward the band centre). Calibrated on synthetic male+female Japanese vowels; 36/40 correct across voice
        // pitches 95-290 Hz in that test. Real voices will do worse; this is a first version.
        static readonly Vector2[] Vowels =
        {
            new Vector2(563f, 1181f), // aa
            new Vector2(361f, 1775f), // ih
            new Vector2(387f, 1252f), // ou
            new Vector2(444f, 1638f), // ee
            new Vector2(469f, 1075f), // oh
        };

        AudioClip _clip;
        string _activeDevice;
        int _rate;
        readonly float[] _samples = new float[N];
        float[] _tail;
        readonly float[] _re = new float[N], _im = new float[N];
        readonly float[] _target = new float[5];

        void OnDisable() => StopMic();

        void Update()
        {
            if (!micEnabled)
            {
                if (_clip != null) StopMic();
                Decay(Time.deltaTime);
                return;
            }
            string want = string.IsNullOrEmpty(device) ? null : device;
            if (_clip == null || want != _activeDevice) StartMic(want);
            if (_clip == null) return;

            if (!ReadLatest()) return;

            // Loudness -> mouth openness, with a noise gate.
            float sum = 0f;
            for (int i = 0; i < N; i++) sum += _samples[i] * _samples[i];
            float db = 20f * Mathf.Log10(Mathf.Sqrt(sum / N) + 1e-7f);
            Level = Mathf.Clamp01((db - gateDb) / Mathf.Max(1f, rangeDb));

            float dt = Time.deltaTime;
            Open = Mathf.Lerp(Open, Level, 1f - Mathf.Exp(-dt / (Level > Open ? Attack : Release)));

            // Vowel shape. Below a whisper there is no usable spectrum, so default to plain "aa".
            for (int i = 0; i < 5; i++) _target[i] = i == 0 ? 1f : 0f;
            if (vowels && Level > 0.1f) EstimateVowel(_target);

            for (int i = 0; i < 5; i++)
            {
                float goal = _target[i] * Open;
                Visemes[i] = Mathf.Lerp(Visemes[i], goal, 1f - Mathf.Exp(-dt / (goal > Visemes[i] ? Attack : Release)));
            }
        }

        void Decay(float dt)
        {
            Level = 0f;
            Open = Mathf.Lerp(Open, 0f, 1f - Mathf.Exp(-dt / Release));
            for (int i = 0; i < 5; i++) Visemes[i] = Mathf.Lerp(Visemes[i], 0f, 1f - Mathf.Exp(-dt / Release));
        }

        void StartMic(string dev)
        {
            StopMic();
            Error = null;
            if (Microphone.devices.Length == 0)
            {
                Error = "No microphone found";
                micEnabled = false;
                return;
            }
            Microphone.GetDeviceCaps(dev, out int minF, out int maxF);
            _rate = minF == 0 && maxF == 0 ? 16000 : Mathf.Clamp(16000, minF, maxF);
            _clip = Microphone.Start(dev, true, 1, _rate);
            if (_clip == null)
            {
                Error = "Could not open the microphone";
                micEnabled = false;
                return;
            }
            _activeDevice = dev;
            Debug.Log($"[Mystic] Lip sync microphone: {(dev ?? "default")} at {_rate} Hz");
        }

        void StopMic()
        {
            if (_clip != null) Microphone.End(_activeDevice);
            _clip = null;
            _activeDevice = null;
        }

        // Copies the newest N samples out of the looping 1 s microphone buffer, handling the wrap-around.
        bool ReadLatest()
        {
            int pos = Microphone.GetPosition(_activeDevice);
            int len = _clip.samples;
            if (pos <= 0 || len < N) return false;
            int start = pos - N;
            if (start >= 0)
            {
                _clip.GetData(_samples, start);
            }
            else
            {
                int head = -start;           // samples from the end of the buffer
                if (_tail == null || _tail.Length != head) _tail = new float[head];
                _clip.GetData(_tail, len - head);
                var front = new float[N - head];
                _clip.GetData(front, 0);
                System.Array.Copy(_tail, 0, _samples, 0, head);
                System.Array.Copy(front, 0, _samples, head, N - head);
            }
            return true;
        }

        // Spectral centroids in the F1 (250-1000 Hz) and F2 (1000-3000 Hz) bands, matched against vowel targets.
        void EstimateVowel(float[] weights)
        {
            float prev = 0f;
            for (int i = 0; i < N; i++)
            {
                float x = _samples[i];
                float pre = x - 0.97f * prev; // pre-emphasis lifts the higher formants
                prev = x;
                float hann = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * i / (N - 1));
                _re[i] = pre * hann;
                _im[i] = 0f;
            }
            Fft(_re, _im);

            float binHz = (float)_rate / N;
            float f1 = Centroid(250f, 1000f, binHz);
            float f2 = Centroid(1000f, 3000f, binHz);
            if (f1 <= 0f || f2 <= 0f) return;

            float total = 0f;
            for (int v = 0; v < 5; v++)
            {
                float d1 = Mathf.Log(f1 / Vowels[v].x), d2 = Mathf.Log(f2 / Vowels[v].y);
                weights[v] = Mathf.Exp(-(d1 * d1 + d2 * d2) / (2f * 0.25f * 0.25f));
                total += weights[v];
            }
            if (total < 1e-6f)
            {
                for (int v = 0; v < 5; v++) weights[v] = v == 0 ? 1f : 0f;
                return;
            }
            for (int v = 0; v < 5; v++) weights[v] /= total;
        }

        float Centroid(float lo, float hi, float binHz)
        {
            int a = Mathf.Max(1, Mathf.FloorToInt(lo / binHz));
            int b = Mathf.Min(N / 2 - 1, Mathf.CeilToInt(hi / binHz));
            float num = 0f, den = 0f;
            for (int k = a; k <= b; k++)
            {
                float p = _re[k] * _re[k] + _im[k] * _im[k];
                num += p * k * binHz;
                den += p;
            }
            return den > 0f ? num / den : 0f;
        }

        // In-place iterative radix-2 FFT (N is a power of two).
        static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2f * Mathf.PI / len;
                float wr = Mathf.Cos(ang), wi = Mathf.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int u = i + k, v = i + k + len / 2;
                        float tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                        re[v] = re[u] - tr; im[v] = im[u] - ti;
                        re[u] += tr; im[u] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
