using ForzaHaptics.Telemetry;

namespace ForzaHaptics.Triggers;

public sealed record TriggerPair(TriggerEffect L2, TriggerEffect R2)
{
    public static readonly TriggerPair Off = new(TriggerEffect.Off, TriggerEffect.Off);
    public override string ToString() => $"L2 {L2} · R2 {R2}";
}

/// <summary>Single-owner trigger clock. Telemetry supplies events; idle and released triggers stay off.</summary>
public sealed class TriggerProcessor
{
    private readonly Channel _left = new(), _right = new();
    private ForzaPacket? _packet;
    private int _previousGear = -1;
    private double _gearTransitionSince = double.NaN;
    private const double GearTransitionTimeoutSeconds = 0.5;
    private bool _collisionHigh, _moving, _upshift;
    private long _shiftSequence, _collisionSequence;
    private double _shiftTime = double.NegativeInfinity, _collisionTime = double.NegativeInfinity;
    private double _lastTick = double.NaN;
    public string Status => $"L2 {_left.Status} · R2 {_right.Status} | shifts seen {_shiftSequence}, started L2 {_left.PlayedShifts} R2 {_right.PlayedShifts}";

    public void Reset()
    {
        _left.Reset(); _right.Reset(); _packet = null;
        _previousGear = -1; _gearTransitionSince = double.NaN;
        _collisionHigh = _moving = false;
        _shiftSequence = _collisionSequence = 0;
        _shiftTime = _collisionTime = double.NegativeInfinity;
        _lastTick = double.NaN;
    }

    public void AcceptTelemetry(ForzaPacket packet, TriggersConfig config, double now)
    {
        if (!double.IsFinite(now) || !packet.HasFiniteFeedbackValues || !packet.TireSlipAngle.IsFinite || !packet.IsRaceOn)
        {
            Reset();
            return;
        }
        // Captured FH6 shifts include a brief raw gear=11 between forward gears.
        // It is not a shift itself. Keep the last forward gear for a bounded window;
        // repeated intermediate packets must not extend that window.
        bool transitioning = !double.IsNaN(_gearTransitionSince);
        if (transitioning && (now < _gearTransitionSince || now - _gearTransitionSince > GearTransitionTimeoutSeconds))
            _previousGear = -1;
        if (packet.Gear is > 0 and < 11)
        {
            if (_previousGear is > 0 and < 11 && packet.Gear != _previousGear)
            {
                _upshift = packet.Gear > _previousGear;
                _shiftTime = now;
                _shiftSequence++;
            }
            _previousGear = packet.Gear;
            _gearTransitionSince = double.NaN;
        }
        else if (packet.Gear == 11)
        {
            if (!transitioning) _gearTransitionSince = now;
        }
        else
        {
            _previousGear = -1;
            _gearTransitionSince = double.NaN;
        }
        bool hit = packet.SmashableVelDiff > config.Collision.ThresholdMps;
        if (_packet != null && hit && !_collisionHigh && now - _collisionTime >= .15)
        {
            _collisionTime = now;
            _collisionSequence++;
        }
        if (hit) _collisionHigh = true;
        else if (packet.SmashableVelDiff <= config.Collision.ThresholdMps * .8f) _collisionHigh = false;
        _packet = packet;
    }

    public TriggerPair Tick(TriggersConfig config, byte leftTrigger, byte rightTrigger, double now)
    {
        if (!double.IsFinite(now) || !config.Enabled || config.Strength <= 0 || !float.IsFinite(config.Strength) ||
            _packet is not { IsRaceOn: true } p || (!double.IsNaN(_lastTick) && (now < _lastTick || now - _lastTick > .3)))
        {
            Reset();
            return TriggerPair.Off;
        }
        float dt = double.IsNaN(_lastTick) ? .01f : (float)(now - _lastTick);
        _lastTick = now;
        _left.UpdatePedal(leftTrigger); _right.UpdatePedal(rightTrigger);
        _moving = MathF.Abs(p.SpeedKmh) > (_moving ? 3 : 5);
        float front = (MathF.Abs(p.TireSlipRatio.FL) + MathF.Abs(p.TireSlipRatio.FR)) * .5f;
        float rear = (MathF.Abs(p.TireSlipRatio.RL) + MathF.Abs(p.TireSlipRatio.RR)) * .5f;
        float driven = p.DrivetrainType switch { 0 => front, 1 => rear, 2 => (front + rear) * .5f, _ => 0 };
        float lateral = MathF.Max((MathF.Abs(p.TireSlipAngle.FL) + MathF.Abs(p.TireSlipAngle.FR)) * .5f,
            (MathF.Abs(p.TireSlipAngle.RL) + MathF.Abs(p.TireSlipAngle.RR)) * .5f);
        var r = config.Throttle;
        var l = config.Brake;
        float band = config.Hysteresis.Enabled ? config.Hysteresis.SlipBand : 0;
        Target? rSlip = _right.Slip(_moving && r.SlipEnabled && !_left.Pedal && p.DrivetrainType is >= 0 and <= 2,
            driven, _moving && r.LateralSlipEnabled, lateral, r.GripLoss, band, r.VibAmpMin, r.VibAmpMax,
            r.VibFreqMin, r.MaxVibration, r.VibAttackMs, r.VibReleaseMs, r.SlipMode, r.PulseDurationMs, r.PulsePauseMs);
        Target? lSlip = _left.Slip(_moving && l.SlipEnabled, (front + rear) * .5f, false, 0, l.GripLoss, band,
            l.VibAmpMin, l.AbsAmpMax, l.MinVibration, l.MaxVibration, l.VibAttackMs, l.VibReleaseMs,
            l.SlipMode, l.PulseDurationMs, l.PulsePauseMs);
        Target? shift = _shiftSequence > 0 ? new(Kind.Gear, _shiftSequence, 8 * config.GearShift.Amp, config.GearShift.Freq,
            config.GearShift.AttackMs / 1000.0, config.GearShift.ReleaseMs / 1000.0,
            config.GearShift.DurationMs / 1000.0, _shiftTime + .15) : null;
        Target? collision = config.Collision.Enabled && _collisionSequence > 0 && now < _collisionTime + config.Collision.DurationMs / 1000.0
            ? new(Kind.Collision, _collisionSequence, 8 * config.Collision.Amp, config.Collision.Freq, .05, .05,
                config.Collision.DurationMs / 1000.0, _collisionTime + config.Collision.DurationMs / 1000.0) : null;
        var gear = config.GearShift;
        TriggerEffect right = _right.Tick(r.Enabled,
            r.Intensity * config.Strength, r.VibSmoothing, rSlip, shift, _upshift ? gear.ThrottleUpshift : gear.ThrottleDownshift,
            collision, Road(p, config.Surface, true), now, dt);
        TriggerEffect left = _left.Tick(l.Enabled,
            l.Intensity * config.Strength, l.VibSmoothing, lSlip, shift, _upshift ? gear.BrakeUpshift : gear.BrakeDownshift,
            collision, Road(p, config.Surface, false), now, dt);
        return new(left, right);
    }

    private static Target? Road(ForzaPacket p, TriggerSurfaceConfig c, bool right)
    {
        if (!(right ? c.Throttle : c.Brake)) return null;
        if (p.WheelOnRumbleStrip.Any(0) || p.WheelOnRumbleStrip.Any(1))
            return new(Kind.Road, 0, 8 * c.StripAmp, c.StripFreq, .08, .08);
        float rumble = (p.SurfaceRumble.FL + p.SurfaceRumble.FR + p.SurfaceRumble.RL + p.SurfaceRumble.RR) * .25f;
        return rumble > 0 ? new(Kind.Road, 0, 8 * c.Amp * Math.Clamp(rumble, 0, 1), c.Freq, .08, .08) : null;
    }

    private enum Kind { Off, Gear, Collision, Slip, Road }
    private sealed record Target(Kind Kind, long Id = 0, float Level = 0, float Frequency = 0,
        double Attack = 0, double Release = 0, double Duration = double.PositiveInfinity,
        double LatestStart = double.PositiveInfinity, bool Repeated = false, double Pause = 0);
    private sealed record Frame(float Level = 0, float Frequency = 0)
    {
        public static readonly Frame Zero = new();
        public bool HasForce => Level > 0;
        public Frame Scale(float factor) => this with { Level = Level * factor };
        public TriggerEffect Encode() => TriggerEffect.Vibration((int)MathF.Round(Frequency),
            (int)MathF.Round(Math.Clamp(Level, 0, 8)));
    }
    private sealed class Channel
    {
        private bool _longitudinal, _lateral;
        private Target? _active, _pendingGear;
        private Frame _last = Frame.Zero, _fadeFrom = Frame.Zero;
        private double _started, _fadeSince = double.NaN, _fadeDuration;
        private long _seenShift, _seenCollision;
        private float _frequency;
        public long PlayedShifts { get; private set; }
        public bool Pedal { get; private set; }
        public string Status { get; private set; } = "off";
        public void UpdatePedal(byte value) => Pedal = value > (Pedal ? 10 : 20);
        public void Reset()
        {
            Pedal = _longitudinal = _lateral = false;
            _active = _pendingGear = null;
            _last = _fadeFrom = Frame.Zero;
            _fadeSince = double.NaN; _started = _fadeDuration = 0;
            _seenShift = _seenCollision = PlayedShifts = 0; _frequency = 0;
            Status = "off";
        }

        public Target? Slip(bool longitudinalAllowed, float longitudinal, bool lateralAllowed, float lateral,
            float threshold, float band, float minimum, float maximum, float frequencyMin, float frequencyMax,
            float attackMs, float releaseMs, TriggerSlipMode pattern, float durationMs, float pauseMs)
        {
            _longitudinal = Pedal && longitudinalAllowed && longitudinal > threshold * (_longitudinal ? 1 - band : 1);
            _lateral = Pedal && lateralAllowed && lateral > threshold * (_lateral ? 1 - band : 1);
            if (!_longitudinal && !_lateral) return null;
            float slip = MathF.Max(_longitudinal ? longitudinal : 0, _lateral ? lateral : 0);
            float amount = Math.Clamp((slip - threshold) / MathF.Max(.001f, 2.5f - threshold), 0, 1);
            return new(Kind.Slip, Level: 8 * (minimum + (maximum - minimum) * amount),
                Frequency: frequencyMin + (frequencyMax - frequencyMin) * amount,
                Attack: attackMs / 1000.0, Release: releaseMs / 1000.0,
                Duration: pattern == TriggerSlipMode.Repeated ? durationMs / 1000.0 : double.PositiveInfinity,
                Repeated: pattern == TriggerSlipMode.Repeated, Pause: pauseMs / 1000.0);
        }

        public TriggerEffect Tick(bool enabled,
            float scale, float frequencyAlpha, Target? slip, Target? shift, bool shiftAllowed, Target? collision,
            Target? road, double now, float dt)
        {
            if (!enabled || !float.IsFinite(scale) || scale <= 0)
            {
                Reset();
                return TriggerEffect.Off;
            }
            var idle = new Target(Kind.Off);
            if (shift != null && shift.Id != _seenShift)
            {
                _seenShift = shift.Id;
                if (Pedal && shiftAllowed && !(_active?.Kind == Kind.Gear && now < _started + _active.Duration) && _pendingGear == null && now <= shift.LatestStart)
                    _pendingGear = shift;
            }
            bool activeGear = _active?.Kind == Kind.Gear && now < _started + _active.Duration;
            if (_pendingGear != null && (now > _pendingGear.LatestStart || !Pedal)) _pendingGear = null;
            if (!Pedal)
            {
                // Release cancels all events immediately without applying passive resistance.
                _active = idle; _pendingGear = null; _fadeSince = double.NaN;
                _started = now; _frequency = 0;
                if (collision != null) _seenCollision = collision.Id;
                _last = Frame.Zero; Status = "off";
                return _last.Encode();
            }
            if (collision != null && ((collision.Id == _seenCollision && !(_active?.Kind == Kind.Collision && _active.Id == collision.Id)) || now > collision.LatestStart)) collision = null;
            if (_active?.Kind == Kind.Gear && !activeGear || _active?.Kind == Kind.Collision && now >= _active.LatestStart)
            {
                _last = Frame.Zero;
                _fadeSince = double.NaN;
            }
            Target desired = activeGear ? _active! : _pendingGear ?? collision ?? slip ?? road ?? idle;
            if (desired.Kind == Kind.Gear && !double.IsNaN(_fadeSince) && _fadeDuration > .05)
            {
                _fadeFrom = _last; _fadeSince = now; _fadeDuration = .05;
            }
            // Zero amplitude/frequency is an explicit Off, never an alternative feedback effect.
            if (desired.Kind != Kind.Off && (desired.Level <= 0 || desired.Frequency <= 0))
                desired = desired with { Level = 0 };
            bool same = _active?.Kind == desired.Kind && _active.Id == desired.Id;
            if (!same && double.IsNaN(_fadeSince))
            {
                if (_last.HasForce)
                {
                    _fadeFrom = _last;
                    _fadeSince = now;
                    _fadeDuration = desired.Kind == Kind.Gear ? .05 : _active?.Release > 0 ? _active.Release : .05;
                }
                else Enter(desired, now);
            }
            if (!double.IsNaN(_fadeSince))
            {
                double progress = (now - _fadeSince) / Math.Max(.001, _fadeDuration);
                if (progress < 1 - 1e-9)
                {
                    _last = _fadeFrom.Scale((float)(1 - progress));
                    Status = $"transition to {desired.Kind.ToString().ToLowerInvariant()}";
                    return _last.Encode();
                }
                Enter(desired, now);
            }
            // Retain event parameters/deadlines, but track continuously changing slip/road intensity.
            if (_active!.Kind is Kind.Slip or Kind.Road or Kind.Off) _active = desired;
            Target current = _active;
            double age = now - _started;
            float envelope = 1;
            {
                if (current.Repeated)
                {
                    double period = Math.Max(.001, current.Duration + current.Pause);
                    age %= period;
                    if (age >= current.Duration) envelope = 0;
                }
                if (current.Attack > 0) envelope *= Math.Clamp((float)(age / current.Attack), 0, 1);
                if (double.IsFinite(current.Duration) && current.Release > 0)
                    envelope *= Math.Clamp((float)((current.Duration - age) / current.Release), 0, 1);
                if (current.Kind == Kind.Collision)
                    envelope *= Math.Clamp((float)((current.LatestStart - now) / Math.Max(.001, current.Release)), 0, 1);
                float alpha = 1 - MathF.Pow(1 - Math.Clamp(frequencyAlpha, 0, 1), dt * 60);
                _frequency = _frequency <= 0 ? current.Frequency : _frequency + (current.Frequency - _frequency) * alpha;
                _last = new(Level: current.Level * scale * envelope, Frequency: _frequency);
            }
            Status = current.Kind.ToString().ToLowerInvariant();
            return _last.Encode();
        }

        private void Enter(Target target, double now)
        {
            _active = target; _started = now; _fadeSince = double.NaN;
            _frequency = target.Frequency;
            if (target.Kind == Kind.Gear) { _pendingGear = null; PlayedShifts++; }
            if (target.Kind == Kind.Collision) _seenCollision = target.Id;
        }
    }
}
