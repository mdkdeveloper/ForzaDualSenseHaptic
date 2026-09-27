using System.Collections.Concurrent;
using ForzaHaptics.Controllers;
using ForzaHaptics.Util;

namespace ForzaHaptics.Emulation;

public enum XboxEmulationState { Off, Starting, Running, Stopping, Failed }

public interface IXboxEmulationService : IDisposable
{
    bool IsEnabled { get; }
    bool IsBusy { get; }
    XboxEmulationState State => IsBusy ? XboxEmulationState.Starting : IsEnabled ? XboxEmulationState.Running : XboxEmulationState.Off;
    string Status { get; }
    string? Error { get; }
    XboxBackend Backend => XboxBackend.ViGEm;
    Task SetBackendAsync(XboxBackend backend) => Task.CompletedTask;
    Task InstallHidMaestroAsync() => Task.CompletedTask;
    Task InitializeAsync();
    Task SetEnabledAsync(bool enabled);
    Task CheckAgainAsync();
    void SetHapticsActive(bool active);
    Task SuspendAsync();
    void Resume();
}

/// <summary>Serializes virtual device, hiding and rumble ownership independently of UDP listening.</summary>
public sealed class XboxEmulationService : IXboxEmulationService
{
    private readonly object _gate = new();
    private readonly IControllerService _controller;
    private readonly IHidHideService _hide;
    private IVirtualXboxFactory _factory;
    private XboxBackend _backend;
    private readonly ImpulsePlayback _playback = new();
    private readonly Func<ControllerSnapshot, IXboxRumbleOutput> _createRumble;
    private readonly Func<IDisposable> _acquireLease;
    private readonly Func<long> _clock;
    private readonly ConcurrentQueue<ControllerInputState> _inputs = new();
    private readonly Timer? _timer;
    private IDisposable? _lease;
    private IVirtualXbox? _xbox;
    private IXboxRumbleOutput? _rumble;
    private Action<XboxFeedback>? _feedbackHandler;
    private ControllerInputState? _latest;
    private readonly ConcurrentQueue<Feedback> _feedback = new();
    private string? _deviceId;
    private string? _pendingRumbleResetDevice;
    private bool _initialized, _prepared, _suspended, _faulted, _neutralized;
    private volatile bool _enabled, _hapticsActive, _disposed;
    private int _busy;
    private volatile XboxEmulationState _state;
    private volatile bool _shuttingDown;
    private TaskCompletionSource? _operationCompletion;
    private long _phaseStarted;
    private long _feedbackEpoch;
    private long _priorityEpoch;
    private volatile string _status = "Off";
    private volatile string? _error;
    private sealed record Feedback(long Epoch, long PriorityEpoch, XboxFeedback Packet);
    private sealed class LifecycleRequiredException : Exception;

    public XboxEmulationService(IControllerService controller)
        : this(controller, new HidHideService(), new VirtualXboxFactory(),
            null, () => new EmulationProcessLease()) { }

    internal XboxEmulationService(IControllerService controller, IHidHideService hide,
        IVirtualXboxFactory factory, Func<ControllerSnapshot, IXboxRumbleOutput>? createRumble,
        Func<IDisposable> acquireLease, Func<long>? clock = null, bool runTimer = true)
    {
        _controller = controller;
        _hide = hide;
        _factory = factory;
        _createRumble = createRumble ?? (snapshot => new DualSenseRumbleOutput(snapshot));
        _acquireLease = acquireLease;
        _clock = clock ?? (() => Environment.TickCount64);
        controller.InputReceived += OnInput;
        if (runTimer) _timer = new Timer(_ => Tick(), null, 4, 4);
    }

    public bool IsEnabled => _enabled;
    public bool IsBusy => State is XboxEmulationState.Starting or XboxEmulationState.Stopping;
    public XboxEmulationState State => _state;
    public string Status => IsBusy ? $"{_status} — {Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _phaseStarted)) / 1000.0:F1} s" : _status;
    public string? Error => _error;
    public XboxBackend Backend => _backend;

    public Task SetBackendAsync(XboxBackend backend) => RunCommand(() =>
    {
        if (!Enum.IsDefined(backend)) throw new ArgumentOutOfRangeException(nameof(backend));
        if (_enabled) throw new InvalidOperationException("Stop Xbox emulation before changing its backend.");
        if (_backend == backend) return;
        Cleanup();
        VerifyPreviousRemoval();
        _factory = backend == XboxBackend.HidMaestro
            ? new HidMaestroXboxFactory() : new VirtualXboxFactory();
        _backend = backend;
        _error = null;
        _faulted = false;
        _status = "Off";
    }, cleanupOnFailure: false);

    public Task InstallHidMaestroAsync() => RunCommand(() =>
    {
        if (_enabled) throw new InvalidOperationException("Stop Xbox emulation before installing or repairing HIDMaestro.");
        Slow(() => { HidMaestroXboxFactory.InstallOrRepair(); return true; });
        _error = null;
        _faulted = false;
        _status = "Off — HIDMaestro installed";
    }, cleanupOnFailure: false);

    private void OnInput(ControllerInputState input)
    {
        Volatile.Write(ref _latest, input);
        if (!_enabled || _disposed) return;
        _inputs.Enqueue(input);
        // A slow driver cannot retain an unbounded queue of obsolete input.
        while (_inputs.Count > 256 && _inputs.TryDequeue(out _)) { }
    }

    private Task RunCommand(Action action, bool cleanupOnFailure = true,
        XboxEmulationState transition = XboxEmulationState.Starting)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            // Reserve synchronously: repeated UI/timer requests cannot queue duplicate work.
            if (_disposed || _shuttingDown || _busy != 0) return Task.CompletedTask;
            _busy = 1;
            _state = transition;
            completion = _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _status = transition == XboxEmulationState.Stopping ? "Stopping Xbox emulation" : "Preparing Xbox emulation";
            Interlocked.Exchange(ref _phaseStarted, Environment.TickCount64);
        }
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    if (_factory is IVirtualXboxLifecycleFactory lifecycle)
                        lifecycle.SetPhaseCallback(SetPhase);
                    if (!_shuttingDown) action();
                }
                catch (Exception ex)
                {
                    if (cleanupOnFailure) Fail(ex);
                    else
                    {
                        _error = ex.Message;
                        Log.Warn("Xbox emulation: " + ex.Message);
                    }
                }
                finally
                {
                    _state = _faulted ? XboxEmulationState.Failed : _xbox != null
                        ? XboxEmulationState.Running : XboxEmulationState.Off;
                    _busy = 0;
                    completion.TrySetResult();
                }
            }
        });
        return completion.Task;
    }

    private void SetPhase(string phase)
    {
        lock (_gate)
        {
            _status = phase;
            Interlocked.Exchange(ref _phaseStarted, Environment.TickCount64);
        }
    }

    // The reserved lifecycle worker is the only creator/disposer. Release the short
    // state lock while Windows blocks, so telemetry handoff and UI snapshots remain usable.
    private T Slow<T>(Func<T> action)
    {
        Monitor.Exit(_gate);
        try { return action(); }
        finally { Monitor.Enter(_gate); }
    }

    private void VerifyPreviousRemoval()
    {
        if (_factory is IVirtualXboxLifecycleFactory lifecycle)
        {
            if (lifecycle.HasPendingRemoval) _state = XboxEmulationState.Stopping;
            Slow(() => { lifecycle.VerifyPreviousRemoval(); return true; });
        }
    }

    public Task InitializeAsync() => RunCommand(InitializeCore);

    private void InitializeCore()
    {
        if (_initialized) return;
        _lease ??= _acquireLease();
        // A recovery failure must prevent a new hiding transaction.
        _hide.Recover();
        _initialized = true;
        _error = null;
        _faulted = false;
        _status = "Off";
    }

    public Task SetEnabledAsync(bool enabled) => RunCommand(() =>
    {
        _enabled = enabled;
        _error = null;
        _faulted = false;
        if (!enabled)
        {
            Cleanup();
            _status = "Off";
            return;
        }
        InitializeCore();
        Prepare();
        TickCore();
    }, transition: enabled ? XboxEmulationState.Starting : XboxEmulationState.Stopping);

    public Task CheckAgainAsync() => RunCommand(() =>
    {
        bool removalRecovery = _factory is IVirtualXboxLifecycleFactory { HasPendingRemoval: true };
        InitializeCore();
        Cleanup();
        VerifyPreviousRemoval();
        _error = null;
        _faulted = false;
        if (removalRecovery)
        {
            _enabled = false;
            _status = "Off — previous controller removal verified";
            return;
        }
        CheckDrivers();
        if (_enabled) { Prepare(); TickCore(); }
        else _status = "Off — drivers ready";
    });

    private void CheckDrivers()
    {
        var errors = new List<string>();
        try { _hide.CheckAvailable(); } catch (Exception ex) { errors.Add(ex.Message); }
        try { Slow(() => { _factory.CheckAvailable(); return true; }); } catch (Exception ex) { errors.Add(ex.Message); }
        if (errors.Count != 0) throw new IOException(string.Join(Environment.NewLine, errors));
    }

    private void Prepare()
    {
        if (_prepared) return;
        CheckDrivers();
        _hide.AllowApplication();
        _prepared = true;
        _status = "Waiting for controller";
    }

    internal void Tick()
    {
        if (_disposed || _shuttingDown || !_enabled || !Monitor.TryEnter(_gate)) return;
        bool lifecycleNeeded = false;
        Exception? failure = null;
        try
        {
            if (_disposed || _shuttingDown || _busy != 0 || _faulted || !_initialized || _suspended) return;
            var snapshot = _controller.Snapshot;
            lifecycleNeeded = !_prepared ||
                (_xbox != null && (!snapshot.IsConnected || snapshot.DeviceId != _deviceId)) ||
                (_xbox == null && snapshot.IsConnected && Fresh(Volatile.Read(ref _latest), snapshot.DeviceId));
            if (!lifecycleNeeded)
                try { TickCore(lifecycleAllowed: false); }
                catch (LifecycleRequiredException) { lifecycleNeeded = true; }
                catch (Exception ex) { failure = ex; }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        if (lifecycleNeeded || failure != null)
        {
            var operation = RunCommand(() =>
            {
                if (failure != null) throw failure;
                TickCore();
            });
            // Deterministic manual clock fixtures wait for their own transition.
            // Production timer callbacks always return immediately.
            if (_timer is null) operation.GetAwaiter().GetResult();
        }
    }

    private bool Fresh(ControllerInputState? input, string? deviceId) => input != null &&
        input.DeviceId == deviceId && _clock() - input.Timestamp is >= 0 and <= 300;

    private void TickCore(bool lifecycleAllowed = true)
    {
        if (!_enabled || _suspended || _shuttingDown) return;
        var snapshot = _controller.Snapshot;
        var latest = Volatile.Read(ref _latest);
        if (!lifecycleAllowed && (!_prepared ||
            (_xbox != null && (!snapshot.IsConnected || snapshot.DeviceId != _deviceId)) ||
            (_xbox == null && snapshot.IsConnected && Fresh(latest, snapshot.DeviceId))))
            throw new LifecycleRequiredException();
        if (_xbox != null && (!snapshot.IsConnected || snapshot.DeviceId != _deviceId)) Cleanup();
        if (!snapshot.IsConnected || snapshot.DeviceId == null)
        {
            _status = "Waiting for controller";
            return;
        }
        Prepare();
        if (_xbox == null)
        {
            if (!Fresh(latest, snapshot.DeviceId))
            {
                _status = "Waiting for controller input";
                return;
            }
            _deviceId = snapshot.DeviceId;
            VerifyPreviousRemoval();
            _state = XboxEmulationState.Starting;
            SetPhase("Creating virtual controller");
            _xbox = Slow(_factory.Create);
            var created = _xbox;
            long epoch = Interlocked.Increment(ref _feedbackEpoch);
            _feedbackHandler = packet =>
            {
                long priority = Interlocked.Read(ref _priorityEpoch);
                if (!packet.IsValid || _disposed || _shuttingDown || !_enabled || _suspended ||
                    epoch != Interlocked.Read(ref _feedbackEpoch) || _hapticsActive ||
                    !Fresh(Volatile.Read(ref _latest), snapshot.DeviceId) ||
                    !_controller.Snapshot.IsInputFreshAt(_clock())) return;
                _feedback.Enqueue(new Feedback(epoch, priority, packet));
                while (_feedback.Count > 1024 && _feedback.TryDequeue(out _)) { }
            };
            created.FeedbackReceived += _feedbackHandler;
            // Driver creation can take seconds. Refresh before submitting physical input.
            snapshot = _controller.Snapshot;
            latest = Volatile.Read(ref _latest);
            if (_shuttingDown || _suspended || !snapshot.IsConnected || snapshot.DeviceId != _deviceId)
            {
                Cleanup();
                _status = "Waiting for controller";
                return;
            }
            if (!Fresh(latest, _deviceId) || !snapshot.IsInputFreshAt(_clock()))
                throw new IOException("Physical input became stale during Xbox startup. Use Check again after the controller reconnects.");
            SubmitInput(latest!);
            SetPhase("Hiding physical controller");
            _hide.Hide(_deviceId);
            _neutralized = false;
            _state = XboxEmulationState.Running;
        }

        if (!Fresh(latest, _deviceId) || !snapshot.IsInputFreshAt(_clock()))
        {
            if (!_neutralized)
            {
                SubmitInput(new ControllerInputState(_deviceId!, _clock(), 0, 0, 0, 0, 0, 0, 0));
                StopRumble();
                Interlocked.Increment(ref _priorityEpoch);
                _neutralized = true;
            }
            _feedback.Clear();
            _inputs.Clear();
            _status = "Connected — input stale";
            return;
        }
        if (_neutralized) SubmitInput(latest!);
        _neutralized = false;
        for (int count = 0; count < 16 && _inputs.TryDequeue(out var input); count++)
            if (Fresh(input, _deviceId)) SubmitInput(input);
        for (int count = 0; count < 256 && _feedback.TryDequeue(out var feedback); count++)
        {
            if (!_hapticsActive && feedback.Epoch == Interlocked.Read(ref _feedbackEpoch) &&
                feedback.PriorityEpoch == Interlocked.Read(ref _priorityEpoch))
            {
                _playback.Accept(feedback.Packet, _clock());
                WritePlayback(snapshot);
            }
        }
        if (!_hapticsActive) WritePlayback(snapshot);
        _status = _hapticsActive ? "Connected — Forza haptics priority" : "Connected";
    }

    private void SubmitInput(ControllerInputState input)
    {
        _xbox!.Submit(input);
    }

    private void WritePlayback(ControllerSnapshot snapshot)
    {
        long now = _clock();
        if (_playback.Select(now) is not { } output) return;
        _rumble ??= _createRumble(snapshot);
        _rumble.Write(output.Large, output.Small, output.Triggers);
        _playback.Written(output, now);
        _pendingRumbleResetDevice = null;
    }

    /// <summary>Must complete before any telemetry/test output is opened. Call false only after output teardown.</summary>
    public void SetHapticsActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hapticsActive = active;
            Interlocked.Increment(ref _priorityEpoch);
            _feedback.Clear();
            _playback.Reset();
            if (active)
            {
                StopRumble();
                if (_pendingRumbleResetDevice != null)
                {
                    var snapshot = _controller.Snapshot;
                    if (snapshot.IsConnected && snapshot.DeviceId == _pendingRumbleResetDevice)
                    {
                        // A previous failed handoff closed its stream but may not have stopped the motors.
                        // Retry the reset before permitting any telemetry output to open.
                        var reset = _createRumble(snapshot);
                        reset.Dispose();
                    }
                    _pendingRumbleResetDevice = null;
                }
            }
        }
    }

    public async Task SuspendAsync()
    {
        Task pending;
        lock (_gate)
        {
            _suspended = true;
            Interlocked.Increment(ref _feedbackEpoch);
            StopRumble();
            pending = _operationCompletion?.Task ?? Task.CompletedTask;
        }
        await pending.ConfigureAwait(false);
        await RunCommand(() =>
        {
            Cleanup();
            _status = _enabled ? "Waiting for controller" : "Off";
        }, transition: XboxEmulationState.Stopping).ConfigureAwait(false);
    }

    public void Resume()
    {
        lock (_gate) _suspended = false;
    }

    private void StopRumble()
    {
        _feedback.Clear();
        _playback.Reset();
        var rumble = _rumble;
        _rumble = null;
        try { rumble?.Dispose(); } // writes zero motors and restores audio haptics synchronously
        catch { _pendingRumbleResetDevice = _deviceId; throw; }
    }

    private void Cleanup(bool verifyPending = true)
    {
        var errors = new List<Exception>();
        _state = XboxEmulationState.Stopping;
        Interlocked.Increment(ref _feedbackEpoch);
        try { StopRumble(); } catch (Exception ex) { errors.Add(ex); }
        var xbox = _xbox;
        _xbox = null;
        if (xbox != null)
        {
            if (_feedbackHandler != null) xbox.FeedbackReceived -= _feedbackHandler;
            try { xbox.Submit(new ControllerInputState(_deviceId!, _clock(), 0, 0, 0, 0, 0, 0, 0)); }
            catch (Exception ex) { errors.Add(ex); }
        }
        _feedbackHandler = null;
        _deviceId = null;
        _inputs.Clear();
        _prepared = false;
        if (_lease != null)
            try { _hide.Restore(); } catch (Exception ex) { errors.Add(ex); }
        if (xbox != null)
        {
            SetPhase("Removing virtual controller");
            try { Slow(() => { xbox.Dispose(); return true; }); } catch (Exception ex) { errors.Add(ex); }
        }
        else if (verifyPending && _lease != null)
            try { VerifyPreviousRemoval(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("Xbox cleanup failed; use Check again to retry recovery.", errors);
    }

    private void Fail(Exception exception)
    {
        string error = exception.Message;
        try { Cleanup(verifyPending: false); } catch (Exception cleanup) { error += Environment.NewLine + cleanup.Message; }
        _faulted = true;
        _state = XboxEmulationState.Failed;
        _error = error;
        _status = "Error";
        Log.Warn("Xbox emulation: " + error);
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed || _shuttingDown) return;
            _shuttingDown = true;
            _enabled = false;
            Interlocked.Increment(ref _feedbackEpoch);
            _controller.InputReceived -= OnInput;
            _timer?.Dispose();
            pending = _operationCompletion?.Task ?? Task.CompletedTask;
        }
        // Never wait while holding the lock needed by the completing worker.
        pending.GetAwaiter().GetResult();
        lock (_gate)
        {
            _disposed = true;
            if (_factory is IVirtualXboxLifecycleFactory lifecycle)
                lifecycle.SetPhaseCallback(SetPhase);
            try { Cleanup(); } catch (Exception ex) { Log.Warn("Xbox shutdown: " + ex.Message); }
            // A second process that failed to acquire ownership must never recover the first one's journal.
            if (_lease != null)
                try { _hide.Dispose(); } catch (Exception ex) { Log.Warn("HidHide shutdown: " + ex.Message); }
            _lease?.Dispose();
            _lease = null;
            _state = XboxEmulationState.Off;
        }
    }
}

/// <summary>A mutex owned by a dedicated thread is released by Windows even after an application crash.</summary>
internal sealed class EmulationProcessLease : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _owner;
    private bool _disposed;

    public EmulationProcessLease()
    {
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _owner = new Thread(() =>
        {
            bool acquired = false;
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, @"Global\ForzaHaptics.XboxEmulation");
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Another ForzaHaptics instance is managing Xbox emulation. Close it and select Check again.");
            }
            catch (Exception ex) { failure = ex; }
            finally { ready.Set(); }
            if (acquired)
            {
                _release.Wait();
                mutex!.ReleaseMutex();
            }
            mutex?.Dispose();
        }) { IsBackground = true, Name = "Xbox emulation ownership" };
        _owner.Start();
        ready.Wait();
        if (failure != null) { _owner.Join(); _release.Dispose(); throw failure; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release.Set();
        _owner.Join();
        _release.Dispose();
    }
}
