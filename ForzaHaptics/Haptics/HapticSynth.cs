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
/// them smoothly independently of the telemetry frame rate.
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
    private long _resetGeneration;

    public SynthMeters Meters { get; } = new();
    public int SampleRate => (int)_fs;

    public HapticSynth(HapticBus bus, Func<AppConfig> config, int sampleRate)
    {
        if (sampleRate <= 2) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _bus = bus;
        _config = config;
        _fs = sampleRate;
        _sides = new[] { new Side(11, _fs), new Side(23, _fs) };
    }

    public void Render(Span<float> left, Span<float> right)
    {
        var t = _bus.ReadTargets(out long generation);
        if (generation != _resetGeneration)
        {
            ResetAudioState(preserveContinuous: true);
            _resetGeneration = generation;
        }
        var c = _config();
        int n = Math.Min(left.Length, right.Length);

        // New impulses
        while (_bus.Kicks.TryDequeue(out var k))
        {
            if (k.Generation != generation) continue;
            bool noise = k.Kind == KickKind.Noise;
            _sides[0].Voices.Add(k.AmpL, k.FreqHz, k.DecaySec, noise, k.EffectName);
            _sides[1].Voices.Add(k.AmpR, k.FreqHz, k.DecaySec, noise, k.EffectName);
        }

        float kAmp = MathX.SmoothingCoef(0.025f, _fs);  // effect levels
        float kFast = MathX.SmoothingCoef(0.006f, _fs); // rumble-strip on/off
        float kFreq = MathX.SmoothingCoef(0.08f, _fs);  // frequencies
        float master = DspGuard.Level(c.MasterGain);
        float slipFreq = DspGuard.Frequency(c.Slip.FreqHz, _fs);
        float spinFreq = DspGuard.Frequency(c.Wheelspin.FreqHz, _fs);
        float lockPulse = DspGuard.Frequency(c.Lockup.PulseHz, _fs), lockCarrier = DspGuard.Frequency(c.Lockup.CarrierHz, _fs);
        float stripCarrier = DspGuard.Frequency(c.RumbleStrip.CarrierHz, _fs), waterFreq = DspGuard.Frequency(c.Water.FreqHz, _fs);
        var dyn = c.Dynamics;
        _compressor.Set(_fs, dyn.Threshold, dyn.Ratio, dyn.AttackMs, dyn.ReleaseMs);
        float makeup = dyn.Enabled ? DspGuard.Level(dyn.Makeup) : 1f;
        float limiterHz = DspGuard.Frequency(c.Engine.LimiterHz, _fs), limiterGain = DspGuard.Level(c.Engine.LimiterGain);
        float roadFreq = DspGuard.Frequency(t.RoadFreq, _fs), stripFreq = DspGuard.Frequency(t.StripFreq, _fs);
        float engineFreq = DspGuard.Frequency(t.EngineFreq, _fs), engineAmp = DspGuard.Level(t.EngineAmp);
        float limiter = Math.Min(1f, DspGuard.Level(t.Limiter));

        for (int s = 0; s < 2; s++)
        {
            _sides[s].HighPass.SetHighPass(_fs, c.LowCutHz);
            _sides[s].LowPass.SetLowPass(_fs, c.HighCutHz);
            _sides[s].Peak = 0f;
        }

        for (int i = 0; i < n; i++)
        {
            // shared parameters
            _roadFreq += (roadFreq - _roadFreq) * kFreq;
            _stripFreq += (stripFreq - _stripFreq) * kFreq;
            _engineFreq += (engineFreq - _engineFreq) * kFreq;
            _engineAmp += (engineAmp - _engineAmp) * kAmp;
            _limiter += (limiter - _limiter) * kAmp;

            // engine (identical on both motors)
            DspGuard.Advance(ref _enginePhase, _engineFreq, _fs);
            float engine = 0f;
            if (_engineAmp > 1e-4f || _limiter > 1e-3f)
            {
                float ph = MathX.TwoPi * _enginePhase;
                engine = _engineAmp * (MathF.Sin(ph) + 0.3f * MathF.Sin(2f * ph));
                if (_limiter > 1e-3f)
                {
                    DspGuard.Advance(ref _limiterPhase, limiterHz, _fs);
                    bool on = _limiterPhase < 0.5f;
                    engine *= on ? 1f + _limiter * 0.5f : 1f - 0.8f * _limiter;
                    if (on) engine += _limiter * limiterGain * MathF.Sin(ph);
                }
            }

            for (int s = 0; s < 2; s++)
            {
                var d = _sides[s];
                d.Road += (DspGuard.Level(t.Road[s]) - d.Road) * kAmp;
                d.Strip += (DspGuard.Level(t.Strip[s]) - d.Strip) * kFast;
                d.Slip += (DspGuard.Level(t.Slip[s]) - d.Slip) * kAmp;
                d.Spin += (DspGuard.Level(t.Spin[s]) - d.Spin) * kAmp;
                d.Lock += (DspGuard.Level(t.Lock[s]) - d.Lock) * kFast;
                d.Water += (DspGuard.Level(t.Water[s]) - d.Water) * kAmp;

                float sum = engine;

                // road: two noise layers whose frequency increases with speed
                if (d.Road > 1e-4f)
                    sum += d.Road * (0.8f * d.RoadA.Next(_roadFreq, _fs) + 0.45f * d.RoadB.Next(_roadFreq * 2.7f, _fs));

                // rumble strip: the speed / spacing rhythm modulates a carrier the actuator reproduces well
                DspGuard.Advance(ref d.StripPhase, _stripFreq, _fs);
                DspGuard.Advance(ref d.StripCarrierPhase, stripCarrier, _fs);
                if (d.Strip > 1e-4f)
                {
                    float env = MathF.Max(0f, MathF.Sin(MathX.TwoPi * d.StripPhase));
                    sum += d.Strip * 1.6f * env * env * MathF.Sin(MathX.TwoPi * d.StripCarrierPhase);
                }

                // slip: a scraping noise with irregular amplitude modulation
                if (d.Slip > 1e-4f)
                    sum += d.Slip * d.SlipNoise.Next(slipFreq, _fs) * (0.5f + MathF.Abs(d.SlipMod.Next(9f, _fs)) * 1.6f);

                // wheelspin: buzzing from a sawtooth wave plus noise
                DspGuard.Advance(ref d.SpinPhase, spinFreq, _fs);
                if (d.Spin > 1e-4f)
                    sum += d.Spin * (0.6f * (2f * d.SpinPhase - 1f) + 0.5f * d.SpinNoise.Next(spinFreq, _fs));

                // lockup: ABS-like pulses
                DspGuard.Advance(ref d.LockPhase, lockPulse, _fs);
                DspGuard.Advance(ref d.LockCarrierPhase, lockCarrier, _fs);
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
            if (!float.IsFinite(left[i]) || !float.IsFinite(right[i]))
            {
                left[i] = right[i] = 0f;
                ResetAudioState(preserveContinuous: false);
            }
            float al = MathF.Abs(left[i]), ar = MathF.Abs(right[i]);
            if (al > _sides[0].Peak) _sides[0].Peak = al;
            if (ar > _sides[1].Peak) _sides[1].Peak = ar;
        }

        if (c.SwapLeftRight)
        {
            for (int i = 0; i < n; i++) (left[i], right[i]) = (right[i], left[i]);
        }

        UpdateMeters(n, c.SwapLeftRight);
        // A reset may arrive while this block is being rendered. Do not emit its old impulses.
        if (_bus.ResetGeneration != generation)
        {
            left.Clear();
            right.Clear();
            ResetAudioState(preserveContinuous: false);
            _resetGeneration = _bus.ResetGeneration;
        }
    }

    private void ResetAudioState(bool preserveContinuous)
    {
        foreach (var side in _sides)
        {
            side.Voices.Reset();
            side.HighPass.Reset();
            side.LowPass.Reset();
            side.RoadA.Reset(); side.RoadB.Reset(); side.SlipNoise.Reset();
            side.SlipMod.Reset(); side.SpinNoise.Reset(); side.WaterNoise.Reset();
            side.Peak = 0f;
            if (!preserveContinuous)
            {
                side.Road = side.Strip = side.Slip = side.Spin = side.Lock = side.Water = 0f;
                side.StripPhase = side.StripCarrierPhase = side.SpinPhase = side.LockPhase = side.LockCarrierPhase = 0f;
            }
        }
        _compressor.Reset();
        if (!preserveContinuous)
        {
            _engineAmp = _enginePhase = _limiter = _limiterPhase = 0f;
            _roadFreq = _stripFreq = _engineFreq = 30f;
        }
        Meters.PeakL = Meters.PeakR = 0f;
        Meters.Active = "";
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
        if (_engineAmp > 0.001f) parts.Add("engine");
        if (_limiter > 0.1f) parts.Add("limiter");
        parts.AddRange(a.Voices.ActiveEffects);
        parts.AddRange(b.Voices.ActiveEffects);
        Meters.Active = string.Join(",", parts.Distinct());
    }
}
