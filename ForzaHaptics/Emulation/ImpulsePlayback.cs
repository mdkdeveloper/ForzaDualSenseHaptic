using ForzaHaptics.Haptics;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Emulation;

internal sealed record EmulationOutput(byte Large, byte Small, TriggerPair? Triggers);

/// <summary>Game-authored envelopes; a body-only update cannot erase independent impulse channels.</summary>
internal sealed class ImpulsePlayback
{
    private sealed record Channel(float Value, long Start, int Duration, int Delay, int Repeats, bool Timed)
    {
        public float At(long now)
        {
            if (!Timed) return Value;
            long elapsed = now - Start;
            long period = (long)Duration + Delay;
            if (elapsed < 0 || Duration <= 0 || period <= 0 || elapsed >= period * (Repeats + 1L)) return 0;
            return elapsed % period < Delay ? 0 : Value;
        }
    }

    private Channel? _large, _small, _left, _right;
    private bool _hasTriggers;
    private EmulationOutput? _written;
    private long _lastTriggerWrite = long.MinValue / 2;

    public void Accept(XboxFeedback feedback, long now)
    {
        if (!feedback.IsValid) return;
        Channel Make(float value) => new(value, feedback.IsTimed ? feedback.Timestamp : now,
            feedback.DurationMs, feedback.DelayMs, feedback.RepeatCount, feedback.IsTimed);
        if (feedback.LargePresent) _large = Make(feedback.Large);
        if (feedback.SmallPresent) _small = Make(feedback.Small);
        if (feedback.LeftTrigger is { } left) { _left = Make(left); _hasTriggers = true; }
        if (feedback.RightTrigger is { } right) { _right = Make(right); _hasTriggers = true; }
    }

    internal static TriggerEffect Map(float magnitude) => !float.IsFinite(magnitude) || magnitude <= 0
        ? TriggerEffect.Off
        : TriggerEffect.Vibration(15, Math.Clamp((int)MathF.Round(Math.Clamp(magnitude, 0, 1) * 8), 1, 8), 1);

    public EmulationOutput? Select(long now)
    {
        if (_large == null && _small == null && !_hasTriggers) return null;
        TriggerPair? triggers = _hasTriggers ? new(Map(_left?.At(now) ?? 0), Map(_right?.At(now) ?? 0)) : null;
        if (triggers != null && _written?.Triggers is { } previous && now - _lastTriggerWrite < 20)
        {
            // Releases bypass the limit, but must not bring the other channel's increase with them.
            triggers = new(
                triggers.L2 == TriggerEffect.Off ? TriggerEffect.Off : previous.L2,
                triggers.R2 == TriggerEffect.Off ? TriggerEffect.Off : previous.R2);
        }
        var desired = new EmulationOutput((byte)(_large?.At(now) ?? 0), (byte)(_small?.At(now) ?? 0), triggers);
        return desired == _written ? null : desired;
    }

    public void Written(EmulationOutput output, long now)
    {
        if (output.Triggers != _written?.Triggers) _lastTriggerWrite = now;
        _written = output;
    }

    public void Reset()
    {
        _large = _small = _left = _right = null;
        _hasTriggers = false;
        _written = null;
        _lastTriggerWrite = long.MinValue / 2;
    }
}
