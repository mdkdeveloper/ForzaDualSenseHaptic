namespace ForzaHaptics.Haptics;

internal static class DspGuard
{
    public static float Level(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 64f) : 0f;
    public static float Frequency(float value, float sampleRate) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, sampleRate * 0.45f) : 0f;

    public static void Advance(ref float phase, float frequency, float sampleRate)
    {
        if (!float.IsFinite(phase)) phase = 0f;
        phase += Frequency(frequency, sampleRate) / sampleRate;
        phase -= MathF.Floor(phase);
    }

    public static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}

/// <summary>Fast random-number generator for the audio thread (xorshift32).</summary>
public struct FastRandom
{
    private uint _state;

    public FastRandom(uint seed)
    {
        _state = seed == 0 ? 0x9E3779B9u : seed;
    }

    /// <summary>Uniformly distributed in [-1, 1).</summary>
    public float NextSigned()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return (x >> 8) * (2f / 16777216f) - 1f;
    }
}

/// <summary>
/// Smoothed noise (value noise with cosine interpolation).
/// Energy is concentrated below freq, with RMS ≈ 0.5 regardless of sample rate.
/// </summary>
public sealed class SmoothNoise
{
    private FastRandom _rng;
    private float _a, _b, _phase;

    public SmoothNoise(uint seed)
    {
        _rng = new FastRandom(seed);
        _a = _rng.NextSigned();
        _b = _rng.NextSigned();
    }

    public float Next(float freqHz, float sampleRate)
    {
        if (!float.IsFinite(sampleRate) || sampleRate <= 0f) return 0f;
        if (!float.IsFinite(_phase) || !float.IsFinite(_a) || !float.IsFinite(_b)) Reset();
        freqHz = DspGuard.Frequency(freqHz, sampleRate);
        _phase += 2f * freqHz / sampleRate; // two random points per cycle
        while (_phase >= 1f)
        {
            _phase -= 1f;
            _a = _b;
            _b = _rng.NextSigned();
        }
        float w = 0.5f - 0.5f * MathF.Cos(MathF.PI * _phase);
        return _a + (_b - _a) * w;
    }

    public void Reset()
    {
        _phase = 0f;
        _a = _rng.NextSigned();
        _b = _rng.NextSigned();
    }
}

/// <summary>Biquad filter (RBJ), transposed direct form II.</summary>
public sealed class Biquad
{
    private float _b0 = 1f, _b1, _b2, _a1, _a2;
    private float _z1, _z2;
    private float _freq = -1f, _fs = -1f;
    private bool _highPass;

    public void SetLowPass(float sampleRate, float freq, float q = 0.7071f) => Set(sampleRate, freq, q, false);

    public void SetHighPass(float sampleRate, float freq, float q = 0.7071f) => Set(sampleRate, freq, q, true);

    private void Set(float fs, float freq, float q, bool highPass)
    {
        if (!float.IsFinite(fs) || fs <= 2f) throw new ArgumentOutOfRangeException(nameof(fs));
        freq = DspGuard.FiniteOr(freq, 1f);
        q = Math.Clamp(DspGuard.FiniteOr(q, 0.7071f), 0.01f, 100f);
        freq = Math.Clamp(freq, 1f, fs * 0.45f);
        if (freq == _freq && fs == _fs && highPass == _highPass) return;
        _freq = freq; _fs = fs; _highPass = highPass;

        float w0 = 2f * MathF.PI * freq / fs;
        float cos = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * q);
        float a0 = 1f + alpha;
        if (highPass)
        {
            _b0 = (1f + cos) * 0.5f / a0;
            _b1 = -(1f + cos) / a0;
            _b2 = (1f + cos) * 0.5f / a0;
        }
        else
        {
            _b0 = (1f - cos) * 0.5f / a0;
            _b1 = (1f - cos) / a0;
            _b2 = (1f - cos) * 0.5f / a0;
        }
        _a1 = -2f * cos / a0;
        _a2 = (1f - alpha) / a0;
    }

    public float Process(float x)
    {
        if (!float.IsFinite(x)) { Reset(); return 0f; }
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (!float.IsFinite(y) || !float.IsFinite(_z1) || !float.IsFinite(_z2))
        {
            Reset();
            return 0f;
        }
        return y;
    }

    public void Reset() => _z1 = _z2 = 0f;
}

/// <summary>
/// Compressor with a peak detector shared by both channels.
/// Above the threshold, level increase is divided by ratio; returns a gain factor ≤ 1.
/// </summary>
public sealed class Compressor
{
    private float _env;
    private float _threshold = 0.25f, _exponent, _attack, _release;

    public void Set(float sampleRate, float threshold, float ratio, float attackMs, float releaseMs)
    {
        if (!float.IsFinite(sampleRate) || sampleRate <= 0f) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _threshold = Math.Clamp(DspGuard.FiniteOr(threshold, 0.25f), 0.01f, 1f);
        ratio = Math.Clamp(DspGuard.FiniteOr(ratio, 3f), 1f, 20f);
        attackMs = Math.Clamp(DspGuard.FiniteOr(attackMs, 3f), 0.1f, 100f);
        releaseMs = Math.Clamp(DspGuard.FiniteOr(releaseMs, 150f), 5f, 2000f);
        _exponent = 1f / ratio - 1f;
        _attack = 1f - MathF.Exp(-1f / (attackMs * 0.001f * sampleRate));
        _release = 1f - MathF.Exp(-1f / (releaseMs * 0.001f * sampleRate));
    }

    public float Gain(float left, float right)
    {
        if (!float.IsFinite(left) || !float.IsFinite(right)) { Reset(); return 0f; }
        if (!float.IsFinite(_env)) Reset();
        float x = MathF.Max(MathF.Abs(left), MathF.Abs(right));
        _env += (x - _env) * (x > _env ? _attack : _release);
        return _env > _threshold ? MathF.Pow(_env / _threshold, _exponent) : 1f;
    }

    public void Reset() => _env = 0f;
}

/// <summary>Pool of short exponentially decaying impulses (sine or noise).</summary>
public sealed class VoicePool
{
    private struct Voice
    {
        public bool Active;
        public bool Noise;
        public float Amp;
        public float Decay;     // multiplier per sample
        public float Attack;    // 0..1
        public float AttackInc;
        public float Phase;
        public float Inc;
        public float NoiseA, NoiseB, NoisePhase;
        public string? EffectName;
    }

    private readonly Voice[] _voices = new Voice[16];
    private readonly float _fs;
    private FastRandom _rng;

    public VoicePool(float sampleRate, uint seed)
    {
        if (!float.IsFinite(sampleRate) || sampleRate <= 2f) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _fs = sampleRate;
        _rng = new FastRandom(seed);
    }

    public void Add(float amp, float freqHz, float decaySec, bool noise, string effectName = "impulse")
    {
        if (!float.IsFinite(amp) || !float.IsFinite(freqHz) || !float.IsFinite(decaySec) ||
            amp < 0.001f || freqHz <= 0f || decaySec <= 0f) return;
        amp = DspGuard.Level(amp);
        freqHz = DspGuard.Frequency(freqHz, _fs);

        // free slot or the quietest one
        int slot = 0;
        float quietest = float.MaxValue;
        for (int i = 0; i < _voices.Length; i++)
        {
            if (!_voices[i].Active) { slot = i; break; }
            if (_voices[i].Amp < quietest) { quietest = _voices[i].Amp; slot = i; }
        }

        decaySec = Math.Clamp(decaySec, 0.005f, 10f);
        _voices[slot] = new Voice
        {
            Active = true,
            Noise = noise,
            Amp = amp,
            Decay = MathF.Exp(-1f / (decaySec * _fs)),
            Attack = 0f,
            AttackInc = 1f / MathF.Max(1f, 0.002f * _fs), // 2 ms attack without clicking
            Phase = 0f,
            Inc = freqHz / _fs,
            NoiseA = 0f,
            NoiseB = _rng.NextSigned(),
            NoisePhase = 0f,
            EffectName = effectName,
        };
    }

    public float Next()
    {
        float sum = 0f;
        for (int i = 0; i < _voices.Length; i++)
        {
            ref Voice v = ref _voices[i];
            if (!v.Active) continue;

            float s;
            if (v.Noise)
            {
                v.NoisePhase += 2f * v.Inc;
                while (v.NoisePhase >= 1f)
                {
                    v.NoisePhase -= 1f;
                    v.NoiseA = v.NoiseB;
                    v.NoiseB = _rng.NextSigned();
                }
                float w = 0.5f - 0.5f * MathF.Cos(MathF.PI * v.NoisePhase);
                s = (v.NoiseA + (v.NoiseB - v.NoiseA) * w) * 1.6f; // match the sine wave's loudness
            }
            else
            {
                s = MathF.Sin(2f * MathF.PI * v.Phase);
                v.Phase += v.Inc;
                if (v.Phase >= 1f) v.Phase -= 1f;
            }

            sum += s * v.Amp * v.Attack;
            if (v.Attack < 1f) v.Attack = MathF.Min(1f, v.Attack + v.AttackInc);
            v.Amp *= v.Decay;
            if (v.Amp < 1e-4f) v.Active = false;
        }
        return sum;
    }

    public IEnumerable<string> ActiveEffects => _voices.Where(v => v.Active)
        .Select(v => v.EffectName ?? "impulse").Distinct();

    public void Reset() => Array.Clear(_voices);
}
