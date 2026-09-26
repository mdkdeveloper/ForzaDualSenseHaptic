using ForzaHaptics.Util;

namespace ForzaHaptics.Haptics;

/// <summary>Signal source for two haptic motors (left, right), with values in [-1, 1].</summary>
public interface IHapticSource
{
    void Render(Span<float> left, Span<float> right);
}

/// <summary>Output levels for the status line.</summary>
public sealed class SynthMeters
{
    public volatile float PeakL, PeakR;
    public volatile string Active = "";
}

/// <summary>
/// Haptic synthesizer. Runs in the audio thread at any sample rate
/// (48 kHz for USB, 3 kHz for Bluetooth). Reads parameters from HapticBus and interpolates
/// them smoothly so 60 Hz telemetry does not produce stair-step artifacts.
/// </summary>
public sealed class HapticSynth : IHapticSource
{
    private sealed class Side
    {
        public readonly SmoothNoise RoadA, RoadB, SlipNoise, SlipMod, SpinNoise, WaterNoise;
        public readonly VoicePool Voices;
        public readonly Biquad HighPass = new(), LowPass = new();
        public float Road, Strip, Slip, Spin, Lock, Water; // smoothed amplitudes
        public float StripPhase, StripCarrierPhase, SpinPhase, LockPhase, LockCarrierPhase;
        public float Peak;

        public Side(uint seed, float fs)
        {
            RoadA = new SmoothNoise(seed * 7 + 1);
            RoadB = new SmoothNoise(seed * 7 + 2);
            SlipNoise = new SmoothNoise(seed * 7 + 3);
            SlipMod = new SmoothNoise(seed * 7 + 4);
            SpinNoise = new SmoothNoise(seed * 7 + 5);
            WaterNoise = new SmoothNoise(seed * 7 + 6);
            Voices = new VoicePool(fs, seed * 7 + 7);
        }
    }

    private readonly HapticBus _bus;
    private readonly Func<AppConfig> _config;
    private readonly float _fs;
    private readonly Side[] _sides;
    private readonly Compressor _compressor = new();

    private float _roadFreq = 40f, _stripFreq = 30f;
    private float _engineAmp, _engineFreq = 30f, _enginePhase, _limiter, _limiterPhase;
    private float _meterDecay = 1f;

    public SynthMeters Meters { get; } = new();
    public int SampleRate => (int)_fs;

    public HapticSynth(HapticBus bus, Func<AppConfig> config, int sampleRate)
    {
        _bus = bus;
        _config = config;
        _fs = sampleRate;
        _sides = new[] { new Side(11, _fs), new Side(23, _fs) };
    }

    public void Render(Span<float> left, Span<float> right)
    {
        var c = _config();
        var t = _bus.Targets;
        int n = Math.Min(left.Length, right.Length);

        // New impulses
        while (_bus.Kicks.TryDequeue(out var k))
        {
            bool noise = k.Kind == KickKind.Noise;
            _sides[0].Voices.Add(k.AmpL, k.FreqHz, k.DecaySec, noise);
            _sides[1].Voices.Add(k.AmpR, k.FreqHz, k.DecaySec, noise);
        }

        float kAmp = MathX.SmoothingCoef(0.025f, _fs);  // effect levels
        float kFast = MathX.SmoothingCoef(0.006f, _fs); // rumble-strip on/off
        float kFreq = MathX.SmoothingCoef(0.08f, _fs);  // frequencies
        float master = c.MasterGain;
        float slipFreq = c.Slip.FreqHz;
        float spinFreq = c.Wheelspin.FreqHz;
        float lockPulse = c.Lockup.PulseHz, lockCarrier = c.Lockup.CarrierHz;
        float stripCarrier = c.RumbleStrip.CarrierHz, waterFreq = c.Water.FreqHz;
        var dyn = c.Dynamics;
        _compressor.Set(_fs, dyn.Threshold, dyn.Ratio, dyn.AttackMs, dyn.ReleaseMs);
        float makeup = dyn.Enabled ? dyn.Makeup : 1f;
        float limiterHz = c.Engine.LimiterHz, limiterGain = c.Engine.LimiterGain;

        for (int s = 0; s < 2; s++)
        {
            _sides[s].HighPass.SetHighPass(_fs, c.LowCutHz);
            _sides[s].LowPass.SetLowPass(_fs, c.HighCutHz);
            _sides[s].Peak = 0f;
        }

        for (int i = 0; i < n; i++)
        {
            // shared parameters
            _roadFreq += (t.RoadFreq - _roadFreq) * kFreq;
            _stripFreq += (t.StripFreq - _stripFreq) * kFreq;
            _engineFreq += (t.EngineFreq - _engineFreq) * kFreq;
            _engineAmp += (t.EngineAmp - _engineAmp) * kAmp;
            _limiter += (t.Limiter - _limiter) * kAmp;

            // engine (identical on both motors)
            _enginePhase += _engineFreq / _fs;
            if (_enginePhase >= 1f) _enginePhase -= 1f;
            float engine = 0f;
            if (_engineAmp > 1e-4f || _limiter > 1e-3f)
            {
                float ph = MathX.TwoPi * _enginePhase;
                engine = _engineAmp * (MathF.Sin(ph) + 0.3f * MathF.Sin(2f * ph));
                if (_limiter > 1e-3f)
                {
                    _limiterPhase += limiterHz / _fs;
                    if (_limiterPhase >= 1f) _limiterPhase -= 1f;
                    bool on = _limiterPhase < 0.5f;
                    engine *= on ? 1f + _limiter * 0.5f : 1f - 0.8f * _limiter;
                    if (on) engine += _limiter * limiterGain * MathF.Sin(ph);
                }
            }

            for (int s = 0; s < 2; s++)
            {
                var d = _sides[s];
                d.Road += (t.Road[s] - d.Road) * kAmp;
                d.Strip += (t.Strip[s] - d.Strip) * kFast;
                d.Slip += (t.Slip[s] - d.Slip) * kAmp;
                d.Spin += (t.Spin[s] - d.Spin) * kAmp;
                d.Lock += (t.Lock[s] - d.Lock) * kFast;
                d.Water += (t.Water[s] - d.Water) * kAmp;

                float sum = engine;

                // road: two noise layers whose frequency increases with speed
                if (d.Road > 1e-4f)
                    sum += d.Road * (0.8f * d.RoadA.Next(_roadFreq, _fs) + 0.45f * d.RoadB.Next(_roadFreq * 2.7f, _fs));

                // rumble strip: the speed / spacing rhythm modulates a carrier the actuator reproduces well
                d.StripPhase += _stripFreq / _fs;
                if (d.StripPhase >= 1f) d.StripPhase -= 1f;
                d.StripCarrierPhase += stripCarrier / _fs;
                if (d.StripCarrierPhase >= 1f) d.StripCarrierPhase -= 1f;
                if (d.Strip > 1e-4f)
                {
                    float env = MathF.Max(0f, MathF.Sin(MathX.TwoPi * d.StripPhase));
                    sum += d.Strip * 1.6f * env * env * MathF.Sin(MathX.TwoPi * d.StripCarrierPhase);
                }

                // slip: a scraping noise with irregular amplitude modulation
                if (d.Slip > 1e-4f)
                    sum += d.Slip * d.SlipNoise.Next(slipFreq, _fs) * (0.5f + MathF.Abs(d.SlipMod.Next(9f, _fs)) * 1.6f);

                // wheelspin: buzzing from a sawtooth wave plus noise
                d.SpinPhase += spinFreq / _fs;
                if (d.SpinPhase >= 1f) d.SpinPhase -= 1f;
                if (d.Spin > 1e-4f)
                    sum += d.Spin * (0.6f * (2f * d.SpinPhase - 1f) + 0.5f * d.SpinNoise.Next(spinFreq, _fs));

                // lockup: ABS-like pulses
                d.LockPhase += lockPulse / _fs;
                if (d.LockPhase >= 1f) d.LockPhase -= 1f;
                d.LockCarrierPhase += lockCarrier / _fs;
                if (d.LockCarrierPhase >= 1f) d.LockCarrierPhase -= 1f;
                if (d.Lock > 1e-4f && d.LockPhase < 0.5f)
                    sum += d.Lock * MathF.Sin(MathX.TwoPi * d.LockCarrierPhase);

                // water: low, viscous noise
                if (d.Water > 1e-4f)
                    sum += d.Water * d.WaterNoise.Next(waterFreq, _fs);

                // impulses
                sum += d.Voices.Next();

                // output path: gain → LowCut..HighCut band
                float y = d.HighPass.Process(sum * master);
                y = d.LowPass.Process(y);
                if (s == 0) left[i] = y; else right[i] = y;
            }

            // compressor shared by both motors → soft limiting
            float g = (dyn.Enabled ? _compressor.Gain(left[i], right[i]) : 1f) * makeup;
            left[i] = MathF.Tanh(left[i] * g);
            right[i] = MathF.Tanh(right[i] * g);
            float al = MathF.Abs(left[i]), ar = MathF.Abs(right[i]);
            if (al > _sides[0].Peak) _sides[0].Peak = al;
            if (ar > _sides[1].Peak) _sides[1].Peak = ar;
        }

        if (c.SwapLeftRight)
        {
            for (int i = 0; i < n; i++) (left[i], right[i]) = (right[i], left[i]);
        }

        UpdateMeters(n, c.SwapLeftRight);
    }

    private void UpdateMeters(int frames, bool swapped)
    {
        // peak with smooth decay so the status line does not flicker
        _meterDecay = MathF.Exp(-frames / (_fs * 0.3f));
        float pl = swapped ? _sides[1].Peak : _sides[0].Peak;
        float pr = swapped ? _sides[0].Peak : _sides[1].Peak;
        Meters.PeakL = MathF.Max(pl, Meters.PeakL * _meterDecay);
        Meters.PeakR = MathF.Max(pr, Meters.PeakR * _meterDecay);

        var a = _sides[0];
        var b = _sides[1];
        var parts = new List<string>(7);
        if (MathF.Max(a.Road, b.Road) > 0.02f) parts.Add("road");
        if (MathF.Max(a.Strip, b.Strip) > 0.02f) parts.Add("rumble strip");
        if (MathF.Max(a.Slip, b.Slip) > 0.02f) parts.Add("slip");
        if (MathF.Max(a.Spin, b.Spin) > 0.02f) parts.Add("wheelspin");
        if (MathF.Max(a.Lock, b.Lock) > 0.02f) parts.Add("lockup");
        if (MathF.Max(a.Water, b.Water) > 0.02f) parts.Add("water");
        if (_limiter > 0.1f) parts.Add("limiter");
        Meters.Active = string.Join(",", parts);
    }
}
