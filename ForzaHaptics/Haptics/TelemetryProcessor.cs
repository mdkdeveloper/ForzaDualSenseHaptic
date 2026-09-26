using System.Numerics;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;
using ForzaHaptics.Util;

namespace ForzaHaptics.Haptics;

/// <summary>Event counters for status reporting and verification.</summary>
public sealed class EffectCounters
{
    public int Packets, Bumps, Shifts, Impacts, Smashes, Splashes;
}

/// <summary>
/// Converts telemetry frames (60–144+ Hz) into haptic parameters:
/// continuous effect levels plus short impulses. Called from a single thread.
/// </summary>
public sealed class TelemetryProcessor
{
    public const double TimeoutSeconds = 0.3;
    private const float G = 9.81f;

    private readonly HapticBus _bus;
    private readonly Func<AppConfig> _config;
    private readonly Func<long> _revision;
    private long _lastRevision;
    private uint _lastTimestamp;
    private int _lastCarOrdinal;
    private bool _hasTimestamp;
    private readonly bool[] _contact = new bool[2];
    private readonly float[] _contactLevel = new float[2];

    private ForzaPacket? _prev;
    private double _lastPacketTime = double.NegativeInfinity;
    private float _avgPacketDt = 1f / 60f;
    private int _prevGear = -1;
    private readonly bool[] _prevWet = new bool[2];
    private double _lastBumpTime = double.NegativeInfinity;
    private double _lastImpactTime = double.NegativeInfinity;
    private bool _impactArmed = true;
    private bool _smashArmed = true;
    private double _lastSmashTime = double.NegativeInfinity;
    private bool _silent = true;

    public EffectCounters Counters { get; } = new();

    public TelemetryProcessor(HapticBus bus, Func<AppConfig> config, Func<long>? revision = null)
    {
        _bus = bus;
        _config = config;
        _revision = revision ?? (() => 0L);
        _lastRevision = _revision();
    }

    public void Process(ForzaPacket p, double now)
    {
        ObserveRevision();
        if (!double.IsFinite(now) || !p.HasFiniteFeedbackValues)
        {
            CheckTimeout(now);
            return;
        }

        if (!p.IsRaceOn)
        {
            Counters.Packets++;
            _bus.Status = new TelemetryStatus(false, p.SpeedKmh, p.CurrentEngineRpm, p.EngineMaxRpm, p.Gear,
                p.SurfaceRumble.Max(), 1f / MathF.Max(_avgPacketDt, 1e-3f), now);
            ResetState();
            _hasTimestamp = false;
            return;
        }

        uint delta = _hasTimestamp ? unchecked(p.TimestampMs - _lastTimestamp) : 0;
        bool carChanged = _hasTimestamp && p.CarOrdinal != _lastCarOrdinal;
        bool backwards = _hasTimestamp && delta > int.MaxValue;
        uint backwardDistance = backwards ? unchecked(_lastTimestamp - p.TimestampMs) : 0;
        if (_hasTimestamp && !carChanged && (delta == 0 || (backwards && backwardDistance <= 250)))
        {
            CheckTimeout(now);
            return; // Duplicate/out-of-order input never renews the valid-frame watchdog.
        }

        double sinceLast = now - _lastPacketTime;
        bool segment = carChanged || backwards || (_hasTimestamp && delta > 250) || sinceLast > 0.25;
        if (segment) ResetState();
        var c = _config();
        Counters.Packets++;
        if (sinceLast > 0 && sinceLast <= 0.25)
        {
            float alpha = 1f - MathF.Pow(0.95f, (float)sinceLast * 60f);
            _avgPacketDt += ((float)sinceLast - _avgPacketDt) * alpha;
        }
        _lastPacketTime = now;
        _lastTimestamp = p.TimestampMs;
        _lastCarOrdinal = p.CarOrdinal;
        _hasTimestamp = true;

        _bus.Status = new TelemetryStatus(p.IsRaceOn, p.SpeedKmh, p.CurrentEngineRpm, p.EngineMaxRpm, p.Gear,
            p.SurfaceRumble.Max(), 1f / MathF.Max(_avgPacketDt, 1e-3f), now);

        ForzaPacket? prev = _prev;
        float dt = prev == null ? 1f / 60f : Math.Max(delta, 1u) / 1000f;

        var t = new HapticTargets();
        float speedKmh = MathF.Max(0f, p.SpeedKmh);
        float moving = MathX.SmoothStep(2f, 15f, speedKmh);
        float throttle = p.Throttle01;
        float brake = p.Brake01;

        // Suspension extension is only a contact heuristic, not a measured grounded flag.
        Span<float> contact = stackalloc float[2];
        for (int side = 0; side < 2; side++)
        {
            float travel = side == 0 ? MathF.Max(p.NormalizedSuspensionTravel.FL, p.NormalizedSuspensionTravel.RL)
                : MathF.Max(p.NormalizedSuspensionTravel.FR, p.NormalizedSuspensionTravel.RR);
            if (prev == null)
            {
                _contact[side] = travel > 0.01f;
                _contactLevel[side] = _contact[side] ? 1f : 0f;
                _prevWet[side] = p.WheelInPuddle.MaxAbs(side) > 0.01f;
            }
            else if (travel > 0.02f) _contact[side] = true;
            else if (travel < 0.005f) _contact[side] = false;
            _contactLevel[side] += ((_contact[side] ? 1f : 0f) - _contactLevel[side]) * (1f - MathF.Exp(-dt / 0.03f));
            contact[side] = _contactLevel[side];
        }

        // --- Road texture ---
        if (c.Road.Enabled)
        {
            float speedNorm = MathX.Clamp01(speedKmh / MathF.Max(1f, c.Road.FullSpeedKmh));
            float speedCurve = MathF.Pow(speedNorm, 0.6f) * moving;
            t.RoadFreq = MathX.Lerp(c.Road.MinFreqHz, c.Road.MaxFreqHz, speedNorm);
            for (int s = 0; s < 2; s++)
            {
                float surface = MathF.Max(0f, p.SurfaceRumble.Avg(s));
                t.Road[s] = c.Road.Gain * speedCurve * (c.Road.AsphaltBase + c.Road.SurfaceScale * surface) * contact[s];
            }
        }

        // --- Rumble strips ---
        if (c.RumbleStrip.Enabled)
        {
            float amp = c.RumbleStrip.Gain * (0.4f + 0.6f * MathX.Clamp01(speedKmh / 150f)) * moving;
            for (int s = 0; s < 2; s++) t.Strip[s] = p.WheelOnRumbleStrip.Any(s) ? amp : 0f;
            t.StripFreq = MathX.Clamp(p.Speed / MathF.Max(0.05f, c.RumbleStrip.SpacingMeters),
                c.RumbleStrip.MinFreqHz, c.RumbleStrip.MaxFreqHz);
        }

        // --- Loss of grip ---
        if (c.Slip.Enabled)
        {
            for (int s = 0; s < 2; s++)
                t.Slip[s] = c.Slip.Gain * MathX.SmoothStep(c.Slip.Start, c.Slip.Full, p.TireCombinedSlip.MaxAbs(s)) * moving * contact[s];
        }

        // --- Wheelspin ---
        if (c.Wheelspin.Enabled && throttle > 0.1f)
        {
            for (int s = 0; s < 2; s++)
                t.Spin[s] = c.Wheelspin.Gain * MathX.SmoothStep(c.Wheelspin.Start, c.Wheelspin.Full, p.TireSlipRatio.MaxAbs(s)) * contact[s];
        }

        // --- Wheel lockup ---
        bool braking = (brake > 0.1f && throttle < 0.3f) || p.HandBrake > 0;
        if (c.Lockup.Enabled && braking)
        {
            for (int s = 0; s < 2; s++)
                t.Lock[s] = c.Lockup.Gain * MathX.SmoothStep(c.Lockup.Start, c.Lockup.Full, p.TireSlipRatio.MaxAbs(s)) * moving * contact[s];
        }

        // --- Water ---
        if (c.Water.Enabled)
        {
            float speedFactor = MathX.Clamp01(speedKmh / 60f);
            Span<float> splash = stackalloc float[2];
            for (int s = 0; s < 2; s++)
            {
                float wet = p.WheelInPuddle.MaxAbs(s);
                bool isWet = wet > 0.01f;
                t.Water[s] = isWet ? c.Water.Gain * (0.3f + 0.7f * speedFactor) * MathF.Max(wet, 0.5f) * moving : 0f;
                if (prev != null && isWet && !_prevWet[s] && speedKmh > 5f) splash[s] = c.Water.SplashGain * (0.3f + 0.7f * speedFactor);
                _prevWet[s] = isWet;
            }
            if (splash[0] > 0f || splash[1] > 0f)
            {
                _bus.Kick(new HapticKick(splash[0] + 0.3f * splash[1], splash[1] + 0.3f * splash[0], c.Water.SplashFreqHz, 0.12f, KickKind.Noise) { EffectName = "splash" });
                Counters.Splashes++;
            }
        }

        // --- Engine ---
        if (c.Engine.Enabled && p.EngineMaxRpm > p.EngineIdleRpm + 100f && p.CurrentEngineRpm > 1f)
        {
            float rpmN = MathX.Clamp01((p.CurrentEngineRpm - p.EngineIdleRpm) / (p.EngineMaxRpm - p.EngineIdleRpm));
            t.EngineFreq = MathX.Lerp(c.Engine.MinFreqHz, c.Engine.MaxFreqHz, rpmN);
            t.EngineAmp = c.Engine.Gain * (0.25f + 0.75f * throttle) * (0.35f + 0.65f * rpmN);
            t.Limiter = p.CurrentEngineRpm >= c.Engine.LimiterThreshold * p.EngineMaxRpm && throttle > 0.7f ? 1f : 0f;
        }

        // --- Suspension impacts (potholes, joints, landings) ---
        if (c.Suspension.Enabled && prev != null)
        {
            Span<float> strength = stackalloc float[2];
            for (int w = 0; w < 4; w++)
            {
                float v = (p.SuspensionTravelMeters[w] - prev.SuspensionTravelMeters[w]) / dt;
                float st = v > 0f ? v : -v * 0.4f; // compression feels stronger than rebound
                int side = w & 1;                  // FL, RL → 0 (left); FR, RR → 1 (right)
                if (st > strength[side]) strength[side] = st;
            }
            float aL = MathX.SmoothStep(c.Suspension.ThresholdMps, c.Suspension.FullMps, strength[0]);
            float aR = MathX.SmoothStep(c.Suspension.ThresholdMps, c.Suspension.FullMps, strength[1]);
            float a = MathF.Max(aL, aR);
            if (a > 0.02f && now - _lastBumpTime >= 0.06)
            {
                _bus.Kick(new HapticKick(c.Suspension.Gain * (aL + 0.3f * aR), c.Suspension.Gain * (aR + 0.3f * aL),
                    c.Suspension.FreqHz, c.Suspension.DecayMs / 1000f, KickKind.Sine) { EffectName = "suspension" });
                _lastBumpTime = now;
                Counters.Bumps++;
            }
        }

        // --- Gear shifts ---
        if (c.GearShift.Enabled && _prevGear >= 0 && p.Gear != _prevGear && speedKmh > 3f)
        {
            float a = c.GearShift.Gain * (0.6f + 0.4f * throttle);
            _bus.Kick(new HapticKick(a, a, c.GearShift.FreqHz, c.GearShift.DecayMs / 1000f, KickKind.Sine) { EffectName = "shift" });
            Counters.Shifts++;
        }
        _prevGear = p.Gear;

        // --- Collisions ---
        float smash = p.SmashableVelDiff;
        if (c.Impact.Enabled && prev != null)
        {
            var acc = new Vector2(p.Acceleration.X, p.Acceleration.Z); // horizontal plane
            float accG = acc.Length() / G;
            float jumpG = (acc - new Vector2(prev.Acceleration.X, prev.Acceleration.Z)).Length() / (G * dt * 60f);
            float amt = MathX.SmoothStep(c.Impact.StartG, c.Impact.FullG, MathF.Max(accG, jumpG));
            bool refractoryOver = now - _lastImpactTime >= 0.15;
            if (amt <= 0.03f) _impactArmed = true;

            if (amt > 0.03f && _impactArmed && refractoryOver)
            {
                // +X: the car was pushed right → the impact came from the left
                float side = MathX.Clamp(acc.X / (acc.Length() + 1e-3f), -1f, 1f);
                float amp = c.Impact.Gain * amt;
                float l = amp * (0.65f + 0.35f * side);
                float r = amp * (0.65f - 0.35f * side);
                _bus.Kick(new HapticKick(l, r, c.Impact.FreqHz, c.Impact.DecayMs / 1000f, KickKind.Sine) { EffectName = "collision" });
                _bus.Kick(new HapticKick(l * 0.5f, r * 0.5f, c.Impact.NoiseFreqHz, 0.04f, KickKind.Noise) { EffectName = "collision" });
                _lastImpactTime = now;
                _impactArmed = false;
                Counters.Impacts++;
            }

            // Destructible objects (fences, signs)
            if (smash <= 0.5f) _smashArmed = true;
            if (smash > 0.5f && _smashArmed && now - _lastSmashTime >= 0.15)
            {
                float amp = c.Impact.Gain * 0.7f * MathX.Clamp01(smash / MathF.Max(0.1f, c.Impact.SmashableFullVel));
                _bus.Kick(new HapticKick(amp, amp, c.Impact.SmashFreqHz, 0.08f, KickKind.Sine) { EffectName = "smash" });
                _bus.Kick(new HapticKick(amp * 0.6f, amp * 0.6f, c.Impact.NoiseFreqHz, 0.05f, KickKind.Noise) { EffectName = "smash" });
                _smashArmed = false;
                _lastSmashTime = now;
                Counters.Smashes++;
            }
        }
        if (prev == null)
        {
            _smashArmed = smash <= 0.5f;
            float initialG = new Vector2(p.Acceleration.X, p.Acceleration.Z).Length() / G;
            _impactArmed = MathX.SmoothStep(c.Impact.StartG, c.Impact.FullG, initialG) <= 0.03f;
        }

        _bus.Targets = t;
        _bus.PublishTriggerTelemetry(p, now);
        _silent = false;
        _prev = p;
    }

    /// <summary>Check after every datagram as well as when no datagrams arrive.</summary>
    public void CheckTimeout(double now)
    {
        ObserveRevision();
        if (!_silent && double.IsFinite(now) && now - _lastPacketTime >= TimeoutSeconds) ResetState();
    }

    private void ObserveRevision()
    {
        long revision = _revision();
        if (revision == _lastRevision) return;
        _lastRevision = revision;
        ResetState();
    }

    private void ResetState()
    {
        _bus.ResetEffects();
        _prev = null;
        _prevGear = -1;
        Array.Clear(_prevWet);
        Array.Clear(_contact);
        Array.Clear(_contactLevel);
        _lastBumpTime = _lastImpactTime = _lastSmashTime = double.NegativeInfinity;
        _impactArmed = _smashArmed = true;
        _silent = true;
    }
}
