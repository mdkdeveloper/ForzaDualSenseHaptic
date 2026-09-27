using System.Net;
using System.Net.Sockets;
using ForzaHaptics.Haptics;
using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;
using ForzaHaptics.Util;
using ForzaHaptics.Controllers;

namespace ForzaHaptics;

/// <summary>Specifies where telemetry comes from and what the engine does when started.</summary>
public sealed record EngineOptions
{
    public bool Simulate { get; init; }
    public bool Test { get; init; }
    public bool TriggerTest { get; init; }
    public string? ReplayPath { get; init; }
    public string? RecordPath { get; init; }
    public int? Port { get; init; }
    public string? Output { get; init; }
    public string? ControllerDeviceId { get; init; }
}

/// <summary>
/// Real-time pipeline: game / simulator / recording → processor → synthesis → controller (haptics + triggers).
/// Used by both the console and the window. Settings are read through a Func on every frame,
/// so changes take effect immediately; port, output, and trigger changes require a restart.
/// </summary>
public sealed class HapticEngine : IDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly Func<long>? _configRevision;
    private readonly Func<ControllerSnapshot>? _controllerInput;
    private ControllerService? _ownedController;
    private readonly object _sync = new();

    private EngineOptions _options = new();
    private HapticBus? _bus;
    private HapticSynth? _synth;
    private TestPattern? _test;
    private IHapticOutput? _output;
    private TriggerHidWriter? _triggers;
    private UdpTelemetryReceiver? _receiver;
    private TelemetryRecorder? _recorder;
    private CancellationTokenSource? _cts;
    private Thread? _feeder;
    private Thread? _triggerLoop;
    private TriggerRuntime? _triggerRuntime;
    private int _port;
    private double _simStart;
    private Timer? _outputMonitor;
    private CancellationTokenSource? _outputCts;
    private bool _outputSuspended;
    private bool _disposed;
    private readonly HashSet<string> _outputErrors = new(StringComparer.Ordinal);
    private readonly Func<AppConfig, EngineOptions, Func<int, IHapticSource>, (IHapticOutput? Output, string? DeviceId)>? _createOutput;

    internal HapticEngine(Func<AppConfig> config, Func<ControllerSnapshot> controllerInput,
        Func<AppConfig, EngineOptions, Func<int, IHapticSource>, (IHapticOutput? Output, string? DeviceId)> createOutput)
        : this(config, null, controllerInput) => _createOutput = createOutput;

    public HapticEngine(Func<AppConfig> config, Func<long>? configRevision = null, Func<ControllerSnapshot>? controllerInput = null)
    {
        _config = config;
        _configRevision = configRevision;
        _controllerInput = controllerInput;
    }

    public bool IsRunning { get; private set; }
    public string? ActiveControllerDeviceId { get; private set; }
    public EngineOptions Options => _options;
    public string OutputDescription => _output?.Description ?? (IsRunning ? "Waiting for controller" : "");
    public string TriggersDescription => _triggers?.Description ?? "";
    public float PeakL => _synth?.Meters.PeakL ?? 0f;
    public float PeakR => _synth?.Meters.PeakR ?? 0f;
    public string ActiveEffects => _synth?.Meters.Active ?? "";
    public TriggerPair Triggers => _triggers != null ? _bus?.Triggers ?? TriggerPair.Off : TriggerPair.Off;
    public bool HasTriggers => _triggers != null;
    internal int? ListeningPort => _receiver?.Port;

    /// <summary>Starts telemetry independently of controller availability. Returns false if the port is busy.</summary>
    public bool Start(EngineOptions options)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            _options = options;
            var cfg = _config();

            var bus = new HapticBus();
            var processor = new TelemetryProcessor(bus, _config, _configRevision);
            _bus = bus;
            _synth = null;
            _test = null;

            _outputSuspended = false;
            _outputErrors.Clear();
            if (_controllerInput == null && _createOutput == null)
                _ownedController ??= new ControllerService(() => _options.Output ?? _config().Output, () => ActiveControllerDeviceId);

            _cts = new CancellationTokenSource();
            _port = options.Port ?? cfg.Port;
            _simStart = Clock.Now;

            try
            {
                if (options.RecordPath != null)
                {
                    _recorder = new TelemetryRecorder(options.RecordPath);
                    Log.Info($"Recording telemetry: {_recorder.Path}");
                }

                if (options.Test)
                {
                    Log.Info("Motor test: left → right → 20–400 Hz sweep → pulses (8-second cycle).");
                }
                else if (options.TriggerTest)
                {
                    Log.Info("Gear-only trigger test (12 seconds, current profile): hold physical L2/R2; " +
                        "0–2s free, upshifts at 2/6/10s, downshifts at 4/8s, then Off. No slip, road, collision or body effects. Channel and gear switches must be enabled.");
                }
                else if (options.ReplayPath != null)
                {
                    var records = TelemetryRecording.Load(options.ReplayPath);
                    if (records.Count == 0)
                    {
                        Log.Error("Recording file is empty");
                        StopCore();
                        return false;
                    }
                    Log.Info($"Playing recording: {records.Count} packets, {records[^1].Time - records[0].Time:0.0} s, looping.");
                    _feeder = StartReplay(records, processor, _cts.Token);
                }
                else
                {
                    try
                    {
                        _receiver = new UdpTelemetryReceiver(_port, cfg.ForwardTo, _recorder,
                            p => processor.Process(p, Clock.Now),
                            () => processor.CheckTimeout(Clock.Now));
                    }
                    catch (SocketException ex)
                    {
                        Log.Error($"Could not open UDP port {_port}: {ex.Message}. " +
                                  "Another application may be using it; change the port in the settings and in the game.");
                        StopCore();
                        return false;
                    }
                    Log.Info($"Listening on UDP port {_port}. In FH6: Settings → HUD and Gameplay → Data Out: On, " +
                             $"Data Out IP: 127.0.0.1, Data Out IP Port: {_port}.");

                    if (options.Simulate)
                    {
                        _feeder = StartSimulator(_port, _cts.Token, out _simStart);
                        Log.Info("Simulation: sending synthetic telemetry to the local port (42-second cycle).");
                    }
                }

                IsRunning = true;
                RefreshOutputCore();
                var sessionToken = _cts.Token;
                _outputMonitor = new Timer(_ =>
                {
                    lock (_sync)
                    {
                        if (!sessionToken.IsCancellationRequested && IsRunning && !_outputSuspended)
                            RefreshOutputCore();
                    }
                }, null, 1000, 1000);
                return true;
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    /// <summary>Release output while preserving telemetry, forwarding, and recording.</summary>
    public void SuspendOutput()
    {
        lock (_sync)
        {
            _outputSuspended = true;
            StopOutputCore();
        }
    }

    public void ResumeOutput()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _outputSuspended = false;
            if (IsRunning) RefreshOutputCore();
        }
    }

    internal void RefreshOutput()
    {
        lock (_sync)
            if (IsRunning && !_outputSuspended) RefreshOutputCore();
    }

    private void RefreshOutputCore()
    {
        try
        {
            var snapshot = _controllerInput?.Invoke() ?? _ownedController?.Snapshot ?? ControllerSnapshot.Disconnected;
            if (_output != null && (!snapshot.IsConnected || snapshot.DeviceId != ActiveControllerDeviceId || !_output.IsAlive))
            {
                StopOutputCore();
                Log.Info("Controller output detached; telemetry remains active.");
            }
            if (_output != null)
            {
                RefreshTriggersCore();
                return;
            }
            if (!snapshot.IsConnected || snapshot.DeviceId == null || _bus == null)
                return;
            var cfg = _config();
            string mode = (_options.Output ?? cfg.Output).Trim().ToLowerInvariant();
            if ((mode == "usb" && snapshot.Transport != ControllerTransport.Usb) ||
                (mode == "bt" && snapshot.Transport != ControllerTransport.Bluetooth))
                return;
            IHapticSource Factory(int sampleRate) => _options.Test
                ? _test = new TestPattern(sampleRate)
                : _synth = new HapticSynth(_bus, _config, sampleRate);
            string? activeDeviceId;
            if (_createOutput != null)
                (_output, activeDeviceId) = _createOutput(cfg, _options with { ControllerDeviceId = snapshot.DeviceId }, Factory);
            else
                _output = OutputFactory.Create(mode, cfg, Factory, out activeDeviceId, snapshot.DeviceId, quiet: true, reportIssue: ReportOutputIssue);
            if (_output == null)
            {
                _synth = null;
                _test = null;
                return;
            }
            ActiveControllerDeviceId = activeDeviceId;
            _bus.Kicks.Clear();
            _bus.TriggerFrames.Clear();
            _bus.PublishTriggers(TriggerPair.Off, _bus.ResetGeneration);
            _output.Start();
            RefreshTriggersCore();
            _outputErrors.Clear();
            Log.Ok("Output: " + _output.Description);
        }
        catch (Exception exception)
        {
            StopOutputCore();
            ReportOutputIssue(exception.Message);
        }
    }

    private void ReportOutputIssue(string message)
    {
        if (_outputErrors.Add(message))
            Log.Warn("Waiting for controller output: " + message);
    }

    private void StopOutputCore()
    {
        ActiveControllerDeviceId = null;
        StopTriggersCore();
        try { _output?.Dispose(); } catch (Exception exception) { Log.Warn("Output cleanup: " + exception.Message); }
        _output = null;
        _synth = null;
        _test = null;
    }

    private void RefreshTriggersCore()
    {
        if (_createOutput != null || _options.Test || !_config().Triggers.Enabled || _bus == null || _output == null)
            return;
        if (_triggers?.IsAlive == true) return;
        StopTriggersCore();
        _triggers = TriggerOutput.Create(_config(), _bus, _output is BluetoothHidOutput, ActiveControllerDeviceId,
            message =>
            {
                if (_lastTriggerError != message) Log.Warn(message);
                _lastTriggerError = message;
            });
        if (_triggers == null) return;
        _outputCts = new CancellationTokenSource();
        _bus.TriggerFrames.Clear();
        _bus.PublishTriggers(TriggerPair.Off, _bus.ResetGeneration);
        _triggerRuntime = new TriggerRuntime(_bus, _config, _configRevision);
        _triggerLoop = StartTriggerLoop(_triggerRuntime, _bus, _outputCts.Token, _options.TriggerTest);
        _triggers.Start();
        _lastTriggerError = null;
        Log.Ok("Triggers: " + _triggers.Description);
    }

    private string? _lastTriggerError;

    private void StopTriggersCore()
    {
        _outputCts?.Cancel();
        _triggerLoop?.Join(1000);
        _triggerLoop = null;
        _triggerRuntime = null;
        try { _triggers?.Dispose(); } catch (Exception exception) { Log.Warn("Trigger cleanup: " + exception.Message); }
        _triggers = null;
        _outputCts?.Dispose();
        _outputCts = null;
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    private void StopCore()
    {
        bool wasRunning = IsRunning;
        IsRunning = false;
        _outputMonitor?.Dispose();
        _outputMonitor = null;
        _cts?.Cancel();
        _receiver?.Dispose();
        _receiver = null;
        _feeder?.Join(1000);
        _feeder = null;
        StopOutputCore();
        if (_recorder != null)
        {
            _recorder.Dispose();
            Log.Info($"Recorded packets: {_recorder.Count} → {_recorder.Path}");
            _recorder = null;
        }
        _cts?.Dispose();
        _cts = null;
        _synth = null;
        _test = null;
        if (wasRunning) Log.Info("Stopped.");
    }

    /// <summary>Status line matching the console output.</summary>
    public string BuildStatus()
    {
        var bus = _bus;
        var output = _output;
        var synth = _synth;
        var triggers = _triggers;
        var test = _test;
        var runtime = _triggerRuntime;
        if (!IsRunning || bus == null) return "Stopped";

        string health = string.Join(" | ", new[] { output?.Health ?? "Waiting for controller", triggers?.Health ?? "" }.Where(h => h.Length > 0));
        string tail = health.Length > 0 ? " | " + health : "";
        if (triggers != null)
            tail += " | " + (_controllerInput?.Invoke() ?? _ownedController?.Snapshot ?? ControllerSnapshot.Disconnected).TriggerFeedbackText;

        if (test != null) return $"TEST: {test.Step}{tail}";
        if (_options.TriggerTest)
            return $"GEAR-ONLY TEST {(Clock.Now - _simStart < 12 ? "running" : "complete; restart to repeat")} | command {bus.Triggers} | {runtime?.Status}{tail}";

        string meters = synth != null ? $"signal peaks L {synth.Meters.PeakL:0.00} R {synth.Meters.PeakR:0.00}" : "";
        string line = FormatTelemetryStatus(bus.Status, Clock.Now, meters, synth?.Meters.Active ?? "",
            triggers != null ? bus.Triggers.ToString() : "", _port, _options.ReplayPath != null);

        if (_options.Simulate) line = $"[{DrivingSimulator.DescribePhase(Clock.Now - _simStart)}] " + line;
        return line + (runtime != null ? " | " + runtime.Status : "") + tail;
    }

    internal static string FormatTelemetryStatus(TelemetryStatus status, double now, string meters,
        string active, string triggerState, int port, bool replay)
    {
        if (now - status.LastPacketTime >= TelemetryProcessor.TimeoutSeconds)
            return replay ? "Telemetry stale: waiting for the next recording frame..."
                : $"Telemetry stale: waiting on UDP port {port}...";
        if (!status.RaceOn) return $"FH6: menu or paused | {meters}";
        return $"{status.PacketsPerSecond,3:0} pkt/s | {status.SpeedKmh,3:0} km/h | {status.Rpm,5:0} rpm | gear {status.Gear} | " +
               $"surface {status.SurfaceRumble:0.00} | {meters}" + (active.Length > 0 ? " | " + active : "") +
               (triggerState.Length > 0 ? " | command " + triggerState : "");
    }

    private static Thread StartSimulator(int port, CancellationToken token, out double startTime)
    {
        double start = Clock.Now;
        startTime = start;
        var thread = new Thread(() =>
        {
            var sim = new DrivingSimulator();
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var target = new IPEndPoint(IPAddress.Loopback, port);
            double next = Clock.Now;
            while (!token.IsCancellationRequested)
            {
                byte[] data = sim.Sample(Clock.Now - start).ToBytes();
                try { udp.Send(data, data.Length, target); } catch (SocketException) { /* ignore */ }

                next += 1.0 / 60.0;
                double wait = next - Clock.Now;
                if (wait > 0) token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait));
                else if (wait < -0.2) next = Clock.Now;
            }
        })
        {
            IsBackground = true,
            Name = "Simulator",
        };
        thread.Start();
        return thread;
    }

    private static Thread StartReplay(List<(double Time, byte[] Data)> records, TelemetryProcessor processor, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            double t0 = records[0].Time;
            double start = Clock.Now;
            while (!token.IsCancellationRequested)
            {
                foreach (var (time, data) in records)
                {
                    if (token.IsCancellationRequested) break;
                    double due = start + (time - t0);
                    while (!token.IsCancellationRequested)
                    {
                        double wait = due - Clock.Now;
                        if (wait <= 0) break;
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(wait, 0.1)));
                        processor.CheckTimeout(Clock.Now);
                    }
                    if (ForzaPacket.TryParse(data, out var packet)) processor.Process(packet, Clock.Now);
                    processor.CheckTimeout(Clock.Now);
                }
                start = Clock.Now + 1.0; // one second of silence between loops
            }
        })
        {
            IsBackground = true,
            Name = "Replay",
        };
        thread.Start();
        return thread;
    }

    // Only the gear changes: the manual test cannot produce slip, road or collision cues.
    internal static ForzaPacket CreateTriggerTestPacket(double age) => new()
    {
        IsRaceOn = true, TimestampMs = (uint)(age * 1000), Speed = 20,
        Gear = (byte)(((int)(age / 2) % 2 == 0) ? 2 : 3), DrivetrainType = 2,
    };

    private Thread StartTriggerLoop(TriggerRuntime runtime, HapticBus bus, CancellationToken token, bool manualTest)
    {
        double start = _simStart;
        var thread = new Thread(() =>
        {
            double next = Clock.Now;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    double now = Clock.Now;
                    if (manualTest && now - start < 12)
                    {
                        double age = now - start;
                        bus.PublishTriggerTelemetry(CreateTriggerTestPacket(age), now);
                    }
                    if (manualTest && now - start >= 12)
                        bus.PublishTriggers(TriggerPair.Off, bus.ResetGeneration);
                    else
                        runtime.Step(now, Environment.TickCount64,
                            _controllerInput?.Invoke() ?? _ownedController?.Snapshot ?? ControllerSnapshot.Disconnected,
                            ActiveControllerDeviceId);
                    next += 0.01;
                    double wait = next - Clock.Now;
                    if (wait > 0) token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait));
                    else if (wait < -0.1) next = Clock.Now;
                }
            }
            catch (Exception ex) { Log.Error("Trigger control stopped: " + ex.Message); }
            finally { bus.PublishTriggers(TriggerPair.Off, bus.ResetGeneration); }
        }) { IsBackground = true, Name = "Physical trigger control" };
        thread.Start();
        return thread;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
        _ownedController?.Dispose();
        _ownedController = null;
    }
}
