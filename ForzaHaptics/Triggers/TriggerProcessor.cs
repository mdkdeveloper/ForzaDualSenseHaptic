using ForzaHaptics.Telemetry;

namespace ForzaHaptics.Triggers;

/// <summary>A pair of effects for L2 (brake) and R2 (throttle). Immutable after publication.</summary>
public sealed record TriggerPair(TriggerEffect L2, TriggerEffect R2)
{
    public static readonly TriggerPair Off = new(TriggerEffect.Off, TriggerEffect.Off);

    public override string ToString() => $"L2 {L2} · R2 {R2}";
}

/// <summary>
/// Telemetry → adaptive trigger effects. Ported from HorizonHaptics GameParsers/parser.py.
///
/// R2 (throttle): resistance from acceleration (+ turbo); vibration during wheelspin.
/// L2 (brake): resistance from pedal pressure; handbrake forms a rigid wall; for ABS, the upper
///   zones are rigid and the lower zones pulse (as in GT7).
/// Priority: impact > gear shift > handbrake/ABS/wheelspin > resistance > surface.
///
/// All transitions use hysteresis (Triggers.Hysteresis): the exit threshold is lower than the entry
/// threshold, and levels and frequencies ignore small fluctuations. Finally, everything is scaled by
/// Triggers.Strength. Called from the telemetry thread.
/// </summary>
public sealed class TriggerProcessor
{
    // Separate EWMA per mode so the 0..8 and 175..255 scales do not mix.
    private float _r2ResN, _r2ResV, _r2Freq;
    private float _l2ResN, _l2Freq;

    // State and value hysteresis.
    private readonly Latch _r2Losing = new(), _r2Vib = new(), _l2Losing = new(), _l2Abs = new();
    private readonly StickyLevel _r2Level = new(), _l2Level = new(), _l2AbsAmp = new();
    private readonly StickyValue _r2VibFreq = new(), _r2VibAmp = new(), _l2AbsFreq = new();
    private readonly StickyValue _r2SurfaceAmp = new(), _l2SurfaceAmp = new();

    // Smooth vibration attack: when the current vibration began.
    private double _r2VibSince = double.NaN, _l2AbsSince = double.NaN;

    private int _prevGear;
    private double _shiftUntil = double.NegativeInfinity;
    private double _collisionUntil = double.NegativeInfinity;

    public TriggerPair Update(ForzaPacket p, TriggersConfig c, double now)
    {
        if (!c.Enabled || !p.IsRaceOn)
        {
            Reset();
            return TriggerPair.Off;
        }

        ArmShift(p, c, now);
        ArmCollision(p, c, now);

        var h = new Bands(c.Hysteresis);
        var s = new Slips(p);
        var l2 = Brake(p, s, c, h, now).Scale(c.Strength);
        var r2 = Throttle(p, s, c, h, now).Scale(c.Strength);
        return new TriggerPair(l2, r2);
    }

    private void Reset()
    {
        _prevGear = 0;
        _r2Losing.Reset(); _r2Vib.Reset(); _l2Losing.Reset(); _l2Abs.Reset();
        _r2Level.Reset(); _l2Level.Reset(); _l2AbsAmp.Reset();
        _r2VibFreq.Reset(); _r2VibAmp.Reset(); _l2AbsFreq.Reset();
        _r2SurfaceAmp.Reset(); _l2SurfaceAmp.Reset();
        _r2VibSince = _l2AbsSince = double.NaN;
    }

    /// <summary>Hysteresis parameters; zero when Enabled is false (behavior without hysteresis).</summary>
    private readonly struct Bands
    {
        public readonly float Slip, Level, Hold;
        public readonly int Pedal, Vib;

        public Bands(TriggerHysteresisConfig h)
        {
            bool on = h.Enabled;
            Slip = on ? h.SlipBand : 0f;
            Pedal = on ? h.PedalBand : 0;
            Level = on ? h.LevelDeadband : 0f;
            Vib = on ? h.VibDeadband : 0;
            Hold = on ? h.HoldMs / 1000f : 0f;
        }

        public float SlipOff(float threshold) => threshold * (1f - Slip);
    }

    /// <summary>Average |TireCombinedSlip| for the front axle, rear axle, and all four wheels.</summary>
    private readonly struct Slips
    {
        public readonly float Front, Rear, All;

        public Slips(ForzaPacket p)
        {
            var t = p.TireCombinedSlip;
            float fl = MathF.Abs(t.FL), fr = MathF.Abs(t.FR), rl = MathF.Abs(t.RL), rr = MathF.Abs(t.RR);
            Front = (fl + fr) / 2f;
            Rear = (rl + rr) / 2f;
            All = (fl + fr + rl + rr) / 4f;
        }
    }

    // --- R2 / throttle ---

    private TriggerEffect Throttle(ForzaPacket p, Slips slip, TriggersConfig c, Bands h, double now)
    {
        var s = c.Throttle;
        if (s.Mode == TriggerMode.Off) return TriggerEffect.Off;

        if (CollisionBurst(c, now) is { } collision) return collision;
        if (c.GearShift.Throttle && ShiftBurst(c, now) is { } shift) return shift;

        float ax = p.Acceleration.X, az = p.Acceleration.Z;
        float avgAccel = MathF.Sqrt(s.TurnAccelScale * ax * ax + s.FwdAccelScale * az * az);
        int accel = p.Accel;

        float gripOff = h.SlipOff(s.GripLoss);
        bool losing = _r2Losing.Update(
            enter: slip.Front > s.GripLoss || (slip.Rear > s.GripLoss && accel > 200),
            stay: slip.Front > gripOff || (slip.Rear > gripOff && accel > 200 - h.Pedal),
            now, h.Hold);

        // Vibrate only under throttle; without throttle, use ordinary resistance (no rigid wall).
        int vibStartOff = s.VibModeStart - Math.Min(h.Pedal, s.VibModeStart);
        bool vib = losing && s.Mode == TriggerMode.Vibration && _r2Vib.Update(
            enter: accel > s.VibModeStart,
            stay: accel > vibStartOff,
            now, h.Hold);

        if (vib)
        {
            // Frequency and amplitude increase with slip; the frequency floor produces a hum instead of knocks.
            float freq = Map(slip.All, s.GripLoss, 5f, s.VibFreqMin, s.MaxVibration);
            float amp = Map(slip.All, s.GripLoss, 2.5f, s.VibAmpMin, s.VibAmpMax);
            _r2Freq = Ewma(freq, _r2Freq, s.VibSmoothing);
            _r2ResV = Ewma(amp, _r2ResV, s.ResistanceSmoothing);
            if (double.IsNaN(_r2VibSince)) _r2VibSince = now;
            float ramp = Attack(now, _r2VibSince, s.VibAttackMs);
            int f = _r2VibFreq.Update(Round255(_r2Freq), h.Vib);
            int r = _r2VibAmp.Update(Round255(_r2ResV * s.Intensity * ramp), h.Vib);
            return TriggerEffect.Vibration(f, r);
        }
        if (!losing) _r2Vib.Reset();
        _r2VibFreq.Reset();
        _r2VibAmp.Reset();
        _r2VibSince = double.NaN;
        _r2Freq = s.VibFreqMin;
        _r2ResV = s.VibAmpMin;

        float res = Map(avgAccel, 0f, s.AccelLimit, s.MinResistance, s.MaxResistance);
        _r2ResN = Ewma(res, _r2ResN, s.ResistanceSmoothing);
        float boost = p.Boost > 0.5f ? s.BoostResistance : 0f;
        int strength = _r2Level.Update(_r2ResN * s.Intensity + boost, h.Level);
        if (strength > 0) return TriggerEffect.Feedback(strength);

        if (c.Surface.Throttle && SurfaceEffect(p, c, _r2SurfaceAmp, h) is { } surface) return surface;
        return TriggerEffect.Off;
    }

    // --- L2 / brake ---

    private TriggerEffect Brake(ForzaPacket p, Slips slip, TriggersConfig c, Bands h, double now)
    {
        var s = c.Brake;
        if (s.Mode == TriggerMode.Off) return TriggerEffect.Off;

        if (CollisionBurst(c, now) is { } collision) return collision;
        if (c.GearShift.Brake && ShiftBurst(c, now) is { } shift) return shift;

        // The handbrake takes priority over regular braking. HandbrakeStrength uses the 0..8 resistance scale.
        if (p.HandBrake > 0) return TriggerEffect.Feedback(s.HandbrakeStrength);

        int brake = p.Brake;
        bool losing = _l2Losing.Update(
            enter: slip.All > s.GripLoss && brake > 100,
            stay: slip.All > h.SlipOff(s.GripLoss) && brake > 100 - h.Pedal,
            now, h.Hold);

        if (losing && s.Mode == TriggerMode.Vibration)
        {
            float freq = Map(slip.All, s.GripLoss, 5f, s.MinVibration, s.MaxVibration);
            _l2Freq = Ewma(freq, _l2Freq, s.VibSmoothing);
            int f = _l2AbsFreq.Update(Round255(_l2Freq), h.Vib);
            bool abs = _l2Abs.Update(
                enter: f >= s.MinVibration,
                stay: f >= s.MinVibration - h.Vib,
                now, h.Hold);
            if (abs)
            {
                if (double.IsNaN(_l2AbsSince)) _l2AbsSince = now;
                float ramp = Attack(now, _l2AbsSince, s.VibAttackMs);
                float target = Map(slip.All, s.GripLoss, 2.5f, 1f, s.AbsAmpMax) * s.Intensity;
                int amp = _l2AbsAmp.Update(1f + (target - 1f) * ramp, h.Level);
                return TriggerEffect.VibrationWall(Math.Max(1, amp), f, s.AbsWallZones, s.AbsWallStrength);
            }
        }
        else
        {
            _l2Abs.Reset();
            _l2AbsFreq.Reset();
            _l2AbsAmp.Reset();
            _l2Freq = s.MinVibration;
        }
        _l2AbsSince = double.NaN;

        // Regular braking: smooth progressive resistance.
        float res = Map(brake, 0f, 255f, s.MinResistance, s.MaxResistance);
        _l2ResN = Ewma(res, _l2ResN, s.ResistanceSmoothing);
        int strength = _l2Level.Update(_l2ResN * s.Intensity, h.Level);
        if (strength > 0) return TriggerEffect.Feedback(strength);

        if (c.Surface.Brake && SurfaceEffect(p, c, _l2SurfaceAmp, h) is { } surface) return surface;
        return TriggerEffect.Off;
    }

    // --- Short impulses (no hysteresis because they are intentionally brief) ---

    private void ArmShift(ForzaPacket p, TriggersConfig c, double now)
    {
        int gear = p.Gear;
        if (_prevGear > 0 && gear > 0 && gear != _prevGear && p.SpeedKmh > 3f)
            _shiftUntil = now + c.GearShift.DurationMs / 1000.0;
        _prevGear = gear;
    }

    private TriggerEffect? ShiftBurst(TriggersConfig c, double now) =>
        now < _shiftUntil ? TriggerEffect.Vibration(c.GearShift.Freq, c.GearShift.Amp) : null;

    private void ArmCollision(ForzaPacket p, TriggersConfig c, double now)
    {
        if (c.Collision.Enabled && p.SmashableVelDiff > c.Collision.ThresholdMps)
            _collisionUntil = now + c.Collision.DurationMs / 1000.0;
    }

    private TriggerEffect? CollisionBurst(TriggersConfig c, double now) =>
        now < _collisionUntil ? TriggerEffect.Vibration(c.Collision.Freq, c.Collision.Amp) : null;

    /// <summary>
    /// Released trigger: rumble strip (WheelOnRumbleStrip is geometry and always works) or road texture
    /// (the game zeros SurfaceRumble when FH6 vibration is disabled).
    /// </summary>
    private static TriggerEffect? SurfaceEffect(ForzaPacket p, TriggersConfig c, StickyValue amp, Bands h)
    {
        var s = c.Surface;
        if (p.WheelOnRumbleStrip.Any(0) || p.WheelOnRumbleStrip.Any(1))
            return TriggerEffect.Vibration(s.StripFreq, s.StripAmp);
        var r = p.SurfaceRumble;
        float rumble = (r.FL + r.FR + r.RL + r.RR) / 4f;
        if (rumble <= 0f)
        {
            amp.Reset();
            return null;
        }
        return TriggerEffect.Vibration(s.Freq, amp.Update(Round255(s.Amp * rumble), h.Vib));
    }

    private static float Map(float x, float inMin, float inMax, float outMin, float outMax)
    {
        if (inMax <= inMin) return outMin;
        float t = Math.Clamp((x - inMin) / (inMax - inMin), 0f, 1f);
        return outMin + t * (outMax - outMin);
    }

    /// <summary>Linear attack from 0 → 1 over attackMs, starting at since.</summary>
    private static float Attack(double now, double since, float attackMs) =>
        attackMs <= 0f ? 1f : (float)Math.Clamp((now - since) * 1000.0 / attackMs, 0.0, 1.0);

    private static float Ewma(float value, float last, float alpha) => alpha * value + (1f - alpha) * last;

    private static int Round255(float v) => Math.Clamp((int)MathF.Round(v), 0, 255);

    // --- Hysteresis ---

    /// <summary>
    /// Schmitt trigger with hold: switches on when enter is true and switches off only when the looser
    /// stay condition is false; after a transition, the state is held for at least hold seconds.
    /// </summary>
    private sealed class Latch
    {
        private bool _state;
        private double _since = double.NegativeInfinity;

        public bool Update(bool enter, bool stay, double now, float hold)
        {
            bool want = _state ? stay : enter;
            if (want != _state && now - _since >= hold)
            {
                _state = want;
                _since = now;
            }
            return _state;
        }

        public void Reset()
        {
            _state = false;
            _since = double.NegativeInfinity;
        }
    }

    /// <summary>Integer 0..8 with a deadband: changes when the value moves by more than 0.5 + deadband.</summary>
    private sealed class StickyLevel
    {
        private int _level;

        public int Update(float value, float deadband)
        {
            value = Math.Clamp(value, 0f, 8f);
            if (MathF.Abs(value - _level) > 0.5f + deadband) _level = (int)MathF.Round(value);
            return _level;
        }

        public void Reset() => _level = 0;
    }

    /// <summary>Byte value that updates only when the change is at least band.</summary>
    private sealed class StickyValue
    {
        private int _value = -1;

        public int Update(int target, int band)
        {
            if (_value < 0 || Math.Abs(target - _value) >= Math.Max(1, band)) _value = target;
            return _value;
        }

        public void Reset() => _value = -1;
    }
}
