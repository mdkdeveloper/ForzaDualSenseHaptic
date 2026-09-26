using System.Collections.Concurrent;
using ForzaHaptics.Triggers;

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
public readonly record struct HapticKick(float AmpL, float AmpR, float FreqHz, float DecaySec, KickKind Kind);

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
    private HapticTargets _targets = HapticTargets.Silent;
    private TelemetryStatus _status = TelemetryStatus.Empty;
    private TriggerPair _triggers = TriggerPair.Off;

    public readonly ConcurrentQueue<HapticKick> Kicks = new();

    public HapticTargets Targets
    {
        get => Volatile.Read(ref _targets);
        set => Volatile.Write(ref _targets, value);
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
        if (Kicks.Count < 64) Kicks.Enqueue(kick);
    }
}
