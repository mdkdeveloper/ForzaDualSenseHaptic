using System.Collections.Concurrent;
using ForzaHaptics.Controllers;
using ForzaHaptics.Util;

namespace ForzaHaptics.Emulation;

public interface IXboxEmulationService : IDisposable
{
    bool IsEnabled { get; }
    bool IsBusy { get; }
    string Status { get; }
    string? Error { get; }
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
    private readonly IVirtualXboxFactory _factory;
    private readonly Func<ControllerSnapshot, IXboxRumbleOutput> _createRumble;
    private readonly Func<IDisposable> _acquireLease;
    private readonly Func<long> _clock;
    private readonly ConcurrentQueue<ControllerInputState> _inputs = new();
    private readonly Timer? _timer;
    private IDisposable? _lease;
    private IVirtualXbox? _xbox;
    private IXboxRumbleOutput? _rumble;
    private Action<byte, byte>? _feedbackHandler;
    private ControllerInputState? _latest;
    private Feedback? _feedback;
    private string? _deviceId;
    private string? _pendingRumbleResetDevice;
    private bool _initialized, _prepared, _suspended, _faulted, _neutralized;
    private volatile bool _enabled, _hapticsActive, _disposed;
    private int _busy;
    private long _feedbackEpoch;
    private long _priorityEpoch;
    private volatile string _status = "Off";
    private volatile string? _error;
    private sealed record Feedback(long Epoch, long PriorityEpoch, byte Large, byte Small);

    public XboxEmulationService(IControllerService controller)
        : this(controller, new HidHideService(), new VirtualXboxFactory(),
            snapshot => new DualSenseRumbleOutput(snapshot), () => new EmulationProcessLease()) { }

    internal XboxEmulationService(IControllerService controller, IHidHideService hide,
        IVirtualXboxFactory factory, Func<ControllerSnapshot, IXboxRumbleOutput> createRumble,
        Func<IDisposable> acquireLease, Func<long>? clock = null, bool runTimer = true)
    {
        _controller = controller;
        _hide = hide;
        _factory = factory;
        _createRumble = createRumble;
        _acquireLease = acquireLease;
        _clock = clock ?? (() => Environment.TickCount64);
        controller.InputReceived += OnInput;
        if (runTimer) _timer = new Timer(_ => Tick(), null, 4, 4);
    }

    public bool IsEnabled => _enabled;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public string Status => _status;
    public string? Error => _error;

    private void OnInput(ControllerInputState input)
    {
        Volatile.Write(ref _latest, input);
        if (!_enabled || _disposed) return;
        _inputs.Enqueue(input);
        // A slow driver cannot retain an unbounded queue of obsolete input.
        while (_inputs.Count > 256) _inputs.TryDequeue(out _);
    }

    private Task RunCommand(Action action) => Task.Run(() =>
    {
        Interlocked.Increment(ref _busy);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { action(); }
                catch (Exception ex) { Fail(ex); }
            }
        }
        finally { Interlocked.Decrement(ref _busy); }
    });

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
    });

    public Task CheckAgainAsync() => RunCommand(() =>
    {
        InitializeCore();
        Cleanup();
        _error = null;
        _faulted = false;
        CheckDrivers();
        if (_enabled) { Prepare(); TickCore(); }
        else _status = "Off — drivers ready";
    });

    private void CheckDrivers()
    {
        var errors = new List<string>();
        try { _hide.CheckAvailable(); } catch (Exception ex) { errors.Add(ex.Message); }
        try { _factory.CheckAvailable(); } catch (Exception ex) { errors.Add(ex.Message); }
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
        if (_disposed || !_enabled || !Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || _faulted || !_initialized || _suspended) return;
            try { TickCore(); } catch (Exception ex) { Fail(ex); }
        }
        finally { Monitor.Exit(_gate); }
    }

    private bool Fresh(ControllerInputState? input, string? deviceId) => input != null &&
        input.DeviceId == deviceId && _clock() - input.Timestamp is >= 0 and <= 300;

    private void TickCore()
    {
        if (!_enabled || _suspended) return;
        var snapshot = _controller.Snapshot;
        var latest = Volatile.Read(ref _latest);
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
            _xbox = _factory.Create();
            var created = _xbox;
            long epoch = Interlocked.Increment(ref _feedbackEpoch);
            _feedbackHandler = (large, small) =>
            {
                long priority = Interlocked.Read(ref _priorityEpoch);
                if (_enabled && !_hapticsActive && !_disposed && epoch == Interlocked.Read(ref _feedbackEpoch) &&
                    Fresh(Volatile.Read(ref _latest), snapshot.DeviceId) && _controller.Snapshot.IsInputFreshAt(_clock()))
                    Volatile.Write(ref _feedback, new Feedback(epoch, priority, large, small));
            };
            created.RumbleReceived += _feedbackHandler;
            created.Submit(latest!);
            _hide.Hide(_deviceId);
            _neutralized = false;
        }

        if (!Fresh(latest, _deviceId) || !snapshot.IsInputFreshAt(_clock()))
        {
            if (!_neutralized)
            {
                _xbox.Submit(new ControllerInputState(_deviceId!, _clock(), 0, 0, 0, 0, 0, 0, 0));
                StopRumble();
                Interlocked.Increment(ref _priorityEpoch);
                _neutralized = true;
            }
            Interlocked.Exchange(ref _feedback, null);
            _inputs.Clear();
            _status = "Connected — input stale";
            return;
        }
        if (_neutralized) _xbox.Submit(latest!);
        _neutralized = false;
        for (int count = 0; count < 16 && _inputs.TryDequeue(out var input); count++)
            if (Fresh(input, _deviceId)) _xbox.Submit(input);
        var feedback = Interlocked.Exchange(ref _feedback, null);
        if (!_hapticsActive && feedback != null && feedback.Epoch == Interlocked.Read(ref _feedbackEpoch) &&
            feedback.PriorityEpoch == Interlocked.Read(ref _priorityEpoch))
        {
            _rumble ??= _createRumble(snapshot);
            _rumble.Write(feedback.Large, feedback.Small);
            _pendingRumbleResetDevice = null;
        }
        _status = _hapticsActive ? "Connected — Forza haptics priority" : "Connected";
    }

    /// <summary>Must complete before any telemetry/test output is opened. Call false only after output teardown.</summary>
    public void SetHapticsActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hapticsActive = active;
            Interlocked.Increment(ref _priorityEpoch);
            Interlocked.Exchange(ref _feedback, null);
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

    public Task SuspendAsync() => RunCommand(() =>
    {
        _suspended = true;
        Cleanup();
        _status = _enabled ? "Waiting for controller" : "Off";
    });

    public void Resume()
    {
        lock (_gate) _suspended = false;
    }

    private void StopRumble()
    {
        Interlocked.Exchange(ref _feedback, null);
        var rumble = _rumble;
        _rumble = null;
        try { rumble?.Dispose(); } // writes zero motors and restores audio haptics synchronously
        catch { _pendingRumbleResetDevice = _deviceId; throw; }
    }

    private void Cleanup()
    {
        var errors = new List<Exception>();
        Interlocked.Increment(ref _feedbackEpoch);
        try { StopRumble(); } catch (Exception ex) { errors.Add(ex); }
        var xbox = _xbox;
        _xbox = null;
        if (xbox != null)
        {
            if (_feedbackHandler != null) xbox.RumbleReceived -= _feedbackHandler;
            try { xbox.Dispose(); } catch (Exception ex) { errors.Add(ex); }
        }
        _feedbackHandler = null;
        _deviceId = null;
        _inputs.Clear();
        _prepared = false;
        if (_lease != null)
            try { _hide.Restore(); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("Xbox cleanup failed; use Check again to retry recovery.", errors);
    }

    private void Fail(Exception exception)
    {
        string error = exception.Message;
        try { Cleanup(); } catch (Exception cleanup) { error += Environment.NewLine + cleanup.Message; }
        _faulted = true;
        _error = error;
        _status = "Error";
        Log.Warn("Xbox emulation: " + error);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _controller.InputReceived -= OnInput;
            _timer?.Dispose();
            try { Cleanup(); } catch (Exception ex) { Log.Warn("Xbox shutdown: " + ex.Message); }
            // A second process that failed to acquire ownership must never recover the first one's journal.
            if (_lease != null)
                try { _hide.Dispose(); } catch (Exception ex) { Log.Warn("HidHide shutdown: " + ex.Message); }
            _lease?.Dispose();
            _lease = null;
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
