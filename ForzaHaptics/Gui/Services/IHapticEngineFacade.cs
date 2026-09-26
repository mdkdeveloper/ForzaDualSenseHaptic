namespace ForzaHaptics.Gui.Services;

/// <summary>Asynchronous boundary around the blocking device engine.</summary>
public interface IHapticEngineFacade : IDisposable
{
    bool IsRunning { get; }
    string? ActiveControllerDeviceId => null;
    string OutputDescription { get; }
    string TriggersDescription { get; }
    bool HasTriggers { get; }
    string TriggerState { get; }
    float PeakLeft { get; }
    float PeakRight { get; }
    string ActiveEffects { get; }

    Task<bool> StartAsync(EngineOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    string BuildStatus();
}

public sealed class HapticEngineFacade : IHapticEngineFacade
{
    private readonly HapticEngine _engine;

    public HapticEngineFacade(Func<AppConfig> config)
        : this(new HapticEngine(config))
    {
    }

    internal HapticEngineFacade(HapticEngine engine)
    {
        _engine = engine;
    }

    public bool IsRunning => _engine.IsRunning;
    public string? ActiveControllerDeviceId => _engine.ActiveControllerDeviceId;
    public string OutputDescription => _engine.OutputDescription;
    public string TriggersDescription => _engine.TriggersDescription;
    public bool HasTriggers => _engine.HasTriggers;
    public string TriggerState => _engine.Triggers.ToString();
    public float PeakLeft => _engine.PeakL;
    public float PeakRight => _engine.PeakR;
    public string ActiveEffects => _engine.ActiveEffects;

    public Task<bool> StartAsync(EngineOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => _engine.Start(options), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        Task.Run(_engine.Stop, cancellationToken);

    public string BuildStatus() => _engine.BuildStatus();

    public void Dispose() => _engine.Dispose();
}
