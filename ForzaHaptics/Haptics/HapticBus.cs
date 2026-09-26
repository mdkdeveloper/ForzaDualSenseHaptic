using System.Collections.Concurrent;
using ForzaHaptics.Triggers;
using ForzaHaptics.Telemetry;

namespace ForzaHaptics.Haptics;

/// <summary>
/// Continuous target effect parameters. Array index: 0 = left motor, 1 = right motor.
/// The object is immutable after publication: the processor creates a new one each time.
/// </summary>
public sealed class HapticTargets
{
    public readonly float[] Road = new float[2];
    public readonly float[] Strip = new float[2];
    public readonly float[] Slip = new float[2];
    public readonly float[] Spin = new float[2];
    public readonly float[] Lock = new float[2];
    public readonly float[] Water = new float[2];

    public float RoadFreq = 40f;
    public float StripFreq = 30f;
    public float EngineAmp;
    public float EngineFreq = 30f;
    public float Limiter;

    public static readonly HapticTargets Silent = new();
}

public enum KickKind
{
    Sine,
    Noise,
}

/// <summary>A short decaying impulse: suspension impact, gear shift, collision, or splash.</summary>
public readonly record struct HapticKick(float AmpL, float AmpR, float FreqHz, float DecaySec, KickKind Kind)
{
    public long Generation { get; init; }
    public string EffectName { get; init; } = "impulse";
}

/// <summary>State snapshot for the status line.</summary>
public sealed record TelemetryStatus(
    bool RaceOn,
    float SpeedKmh,
    float Rpm,
    float MaxRpm,
    int Gear,
    float SurfaceRumble,
    float PacketsPerSecond,
    double LastPacketTime)
{
    public static readonly TelemetryStatus Empty = new(false, 0, 0, 0, 0, 0, 0, double.NegativeInfinity);
}

/// <summary>
/// Channel between the telemetry thread (UDP) and audio thread (synthesis).
/// Continuous parameters use atomic reference replacement; impulses use a queue.
/// </summary>
public sealed class HapticBus
{
    internal sealed record TriggerFrame(ForzaPacket Packet, double Time, long Generation, long Sequence);
    internal readonly ConcurrentQueue<TriggerFrame> TriggerFrames = new();
    private readonly object _triggerSync = new();
    private long _triggerSequence;
    internal void PublishTriggerTelemetry(ForzaPacket packet, double now)
    {
        lock (_triggerSync)
        {
            // Bound memory if triggers are disabled. A sequence gap resets the consumer's event baseline.
            if (TriggerFrames.Count >= 256) TriggerFrames.Clear();
            TriggerFrames.Enqueue(new(packet.CopyForTriggers(), now, ResetGeneration, ++_triggerSequence));
        }
    }

    internal bool TryReadTriggerTelemetry(double now, out TriggerFrame frame)
    {
        lock (_triggerSync)
        {
            // UDP may publish after the consumer sampled its clock. Keep that frame for
            // the next tick instead of dropping it and creating a sequence gap.
            if (TriggerFrames.TryPeek(out var next) && next.Time <= now &&
                TriggerFrames.TryDequeue(out var ready))
            {
                frame = ready;
                return true;
            }
            frame = null!;
            return false;
        }
    }

    internal void PublishTriggers(TriggerPair pair, long generation)
    {
        lock (_triggerSync)
            if (ResetGeneration == generation) Triggers = pair;
    }
    // One telemetry producer publishes generation and targets together; the audio thread never
    // observes a completed reset generation paired with the preceding segment's targets.
    private sealed record EffectSnapshot(long Generation, HapticTargets Targets);
    private EffectSnapshot _effects = new(0, HapticTargets.Silent);
    public long ResetGeneration => Volatile.Read(ref _effects).Generation;

    internal HapticTargets ReadTargets(out long generation)
    {
        var snapshot = Volatile.Read(ref _effects);
        generation = snapshot.Generation;
        return snapshot.Targets;
    }

    public void ResetEffects()
    {
        lock (_triggerSync)
        {
            Triggers = TriggerPair.Off;
            TriggerFrames.Clear();
            Kicks.Clear();
            var previous = Volatile.Read(ref _effects);
            Volatile.Write(ref _effects, new EffectSnapshot(unchecked(previous.Generation + 1), HapticTargets.Silent));
        }
    }

    private TelemetryStatus _status = TelemetryStatus.Empty;
    private TriggerPair _triggers = TriggerPair.Off;

    public readonly ConcurrentQueue<HapticKick> Kicks = new();

    public HapticTargets Targets
    {
        get => Volatile.Read(ref _effects).Targets;
        set
        {
            var previous = Volatile.Read(ref _effects);
            Volatile.Write(ref _effects, new EffectSnapshot(previous.Generation, value));
        }
    }

    public TelemetryStatus Status
    {
        get => Volatile.Read(ref _status);
        set => Volatile.Write(ref _status, value);
    }

    /// <summary>Current L2/R2 adaptive-trigger effects.</summary>
    public TriggerPair Triggers
    {
        get => Volatile.Read(ref _triggers);
        set => Volatile.Write(ref _triggers, value);
    }

    public void Kick(HapticKick kick)
    {
        if (Kicks.Count < 64) Kicks.Enqueue(kick with { Generation = ResetGeneration });
    }
}
