using ForzaHaptics.Controllers;
using ForzaHaptics.Haptics;

namespace ForzaHaptics.Triggers;

/// <summary>Single consumer for accepted telemetry, physical input and time-based trigger envelopes.</summary>
internal sealed class TriggerRuntime
{
    private readonly HapticBus _bus;
    private readonly Func<AppConfig> _config;
    private readonly Func<long> _revision;
    private readonly TriggerProcessor _processor = new();
    private long _generation = -1, _lastSequence, _lastRevision;
    private double _lastTelemetry = double.NegativeInfinity;
    private string _status = "waiting for physical input and telemetry";
    public string Status => Volatile.Read(ref _status);

    public TriggerRuntime(HapticBus bus, Func<AppConfig> config, Func<long>? revision = null)
    {
        _bus = bus;
        _config = config;
        _revision = revision ?? (() => 0);
        _lastRevision = _revision();
    }

    public void Step(double now, long inputClock, ControllerSnapshot input, string? deviceId)
    {
        long generation = _bus.ResetGeneration;
        long revision = _revision();
        if (generation != _generation || revision != _lastRevision)
        {
            Reset();
            _generation = generation;
            if (revision != _lastRevision)
            {
                _lastRevision = revision;
                _bus.TriggerFrames.Clear();
                Off(generation, "profile changed; waiting for a new frame");
                return;
            }
        }
        if (!double.IsFinite(now) || deviceId == null || input.DeviceId != deviceId || !input.IsInputFreshAt(inputClock))
        {
            Reset();
            _bus.TriggerFrames.Clear();
            Off(generation, "physical input unavailable or stale");
            return;
        }
        var config = _config().Triggers;
        while (_bus.TryReadTriggerTelemetry(now, out var frame))
        {
            if (frame.Generation != generation || now - frame.Time >= 0.3) continue;
            if (_lastSequence != 0 && frame.Sequence != _lastSequence + 1) Reset();
            _lastSequence = frame.Sequence;
            _processor.AcceptTelemetry(frame.Packet, config, frame.Time);
            _lastTelemetry = frame.Time;
        }
        if (now - _lastTelemetry >= 0.3 || !config.Enabled)
        {
            Reset();
            Off(generation, "telemetry unavailable or triggers disabled");
            return;
        }
        var pair = _processor.Tick(config, input.LeftTrigger, input.RightTrigger, now);
        _bus.PublishTriggers(pair, generation);
        Volatile.Write(ref _status, $"physical L2 {input.LeftTrigger}/255 R2 {input.RightTrigger}/255 | {_processor.Status}");
    }

    private void Off(long generation, string reason)
    {
        _bus.PublishTriggers(TriggerPair.Off, generation);
        Volatile.Write(ref _status, reason);
    }

    private void Reset()
    {
        _processor.Reset();
        _lastSequence = 0;
        _lastTelemetry = double.NegativeInfinity;
    }
}
