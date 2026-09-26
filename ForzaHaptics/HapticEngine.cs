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

    public HapticEngine(Func<AppConfig> config, Func<long>? configRevision = null, Func<ControllerSnapshot>? controllerInput = null)
    {
        _config = config;
        _configRevision = configRevision;
        _controllerInput = controllerInput;
    }

    public bool IsRunning { get; private set; }
    public string? ActiveControllerDeviceId { get; private set; }
    public EngineOptions Options => _options;
    public string OutputDescription => _output?.Description ?? "";
    public string TriggersDescription => _triggers?.Description ?? "";
    public float PeakL => _synth?.Meters.PeakL ?? 0f;
    public float PeakR => _synth?.Meters.PeakR ?? 0f;
    public string ActiveEffects => _synth?.Meters.Active ?? "";
    public TriggerPair Triggers => _triggers != null && _bus != null ? _bus.Triggers : TriggerPair.Off;
    public bool HasTriggers => _triggers != null;

    /// <summary>Starts the engine. Returns false when no output is found or the port is busy (the reason is logged).</summary>
    public bool Start(EngineOptions options)
    {
        lock (_sync)
        {
            if (IsRunning) StopCore();
            _options = options;
            var cfg = _config();

            var bus = new HapticBus();
            var processor = new TelemetryProcessor(bus, _config, _configRevision);
            _bus = bus;
            _synth = null;
            _test = null;

            IHapticSource Factory(int sampleRate)
            {
                if (options.Test) return _test = new TestPattern(sampleRate);
                return _synth = new HapticSynth(bus, _config, sampleRate);
            }

            string mode = (options.Output ?? cfg.Output).Trim().ToLowerInvariant();
            _output = OutputFactory.Create(mode, cfg, Factory, out string? activeDeviceId, options.ControllerDeviceId);
            if (_output == null) return false;
            ActiveControllerDeviceId = activeDeviceId;
            Log.Ok("Output: " + _output.Description);

            _triggers = options.Test ? null : TriggerOutput.Create(cfg, bus, _output is BluetoothHidOutput, activeDeviceId);
            if (_triggers != null) Log.Ok("Triggers: " + _triggers.Description);
            if (_triggers != null)
            {
                ActiveControllerDeviceId = _triggers.DeviceId;
                if (_controllerInput == null)
                    _ownedController ??= new ControllerService(() => _config().Output, () => ActiveControllerDeviceId);
                _triggerRuntime = new TriggerRuntime(bus, _config, _configRevision);
            }

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

                _output.Start();
                if (_triggerRuntime != null)
                    _triggerLoop = StartTriggerLoop(_triggerRuntime, bus, _cts.Token, options.TriggerTest);
                _triggers?.Start();
                IsRunning = true;
                return true;
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    private void StopCore()
    {
        bool wasRunning = IsRunning;
        IsRunning = false;
        ActiveControllerDeviceId = null;
        _cts?.Cancel();
        _receiver?.Dispose();
        _receiver = null;
        _feeder?.Join(1000);
        _feeder = null;
        _triggerLoop?.Join(1000);
        _triggerLoop = null;
        _triggerRuntime = null;
        _triggers?.Dispose(); // releases the triggers
        _triggers = null;
        _output?.Dispose();
        _output = null;
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
        if (!IsRunning || _bus == null || _output == null) return "Stopped";

        string health = string.Join(" | ", new[] { _output.Health, _triggers?.Health ?? "" }.Where(h => h.Length > 0));
        string tail = health.Length > 0 ? " | " + health : "";
        if (_triggers != null)
            tail += " | " + (_controllerInput?.Invoke() ?? _ownedController?.Snapshot ?? ControllerSnapshot.Disconnected).TriggerFeedbackText;

        if (_test != null) return $"TEST: {_test.Step}{tail}";
        if (_options.TriggerTest)
            return $"GEAR-ONLY TEST {(Clock.Now - _simStart < 12 ? "running" : "complete; restart to repeat")} | command {_bus.Triggers} | {_triggerRuntime?.Status}{tail}";

        string meters = _synth != null ? $"signal peaks L {_synth.Meters.PeakL:0.00} R {_synth.Meters.PeakR:0.00}" : "";
        string line = FormatTelemetryStatus(_bus.Status, Clock.Now, meters, _synth?.Meters.Active ?? "",
            _triggers != null ? _bus.Triggers.ToString() : "", _port, _options.ReplayPath != null);

        if (_options.Simulate) line = $"[{DrivingSimulator.DescribePhase(Clock.Now - _simStart)}] " + line;
        return line + (_triggerRuntime != null ? " | " + _triggerRuntime.Status : "") + tail;
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
        double start = Clock.Now;
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
        Stop();
        _ownedController?.Dispose();
        _ownedController = null;
    }
}
