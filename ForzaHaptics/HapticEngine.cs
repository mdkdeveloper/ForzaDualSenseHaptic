using System.Net;
using System.Net.Sockets;
using ForzaHaptics.Haptics;
using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;
using ForzaHaptics.Util;

namespace ForzaHaptics;

/// <summary>Specifies where telemetry comes from and what the engine does when started.</summary>
public sealed record EngineOptions
{
    public bool Simulate { get; init; }
    public bool Test { get; init; }
    public string? ReplayPath { get; init; }
    public string? RecordPath { get; init; }
    public int? Port { get; init; }
    public string? Output { get; init; }
}

/// <summary>
/// Real-time pipeline: game / simulator / recording → processor → synthesis → controller (haptics + triggers).
/// Used by both the console and the window. Settings are read through a Func on every frame,
/// so changes take effect immediately; port, output, and trigger changes require a restart.
/// </summary>
public sealed class HapticEngine : IDisposable
{
    private readonly Func<AppConfig> _config;
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
    private int _port;
    private double _simStart;

    public HapticEngine(Func<AppConfig> config)
    {
        _config = config;
    }

    public bool IsRunning { get; private set; }
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
            var processor = new TelemetryProcessor(bus, _config);
            _bus = bus;
            _synth = null;
            _test = null;

            IHapticSource Factory(int sampleRate)
            {
                if (options.Test) return _test = new TestPattern(sampleRate);
                return _synth = new HapticSynth(bus, _config, sampleRate);
            }

            string mode = (options.Output ?? cfg.Output).Trim().ToLowerInvariant();
            _output = OutputFactory.Create(mode, cfg, Factory);
            if (_output == null) return false;
            Log.Ok("Output: " + _output.Description);

            _triggers = options.Test ? null : TriggerOutput.Create(cfg, bus, _output is BluetoothHidOutput);
            if (_triggers != null) Log.Ok("Triggers: " + _triggers.Description);

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
        _cts?.Cancel();
        _receiver?.Dispose();
        _receiver = null;
        _feeder?.Join(1000);
        _feeder = null;
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

        if (_test != null) return $"TEST: {_test.Step}{tail}";

        string meters = _synth != null ? $"L {_synth.Meters.PeakL:0.00} R {_synth.Meters.PeakR:0.00}" : "";
        var s = _bus.Status;
        double age = Clock.Now - s.LastPacketTime;

        string line;
        if (age > 2.0)
        {
            line = _options.ReplayPath != null ? "Pausing between recording loops..." : $"Waiting for telemetry on UDP port {_port}...";
        }
        else if (!s.RaceOn)
        {
            line = $"FH6: menu or paused | {meters}";
        }
        else
        {
            string active = _synth?.Meters.Active ?? "";
            line = $"{s.PacketsPerSecond,3:0} pkt/s | {s.SpeedKmh,3:0} km/h | {s.Rpm,5:0} rpm | gear {s.Gear} | " +
                   $"surface {s.SurfaceRumble:0.00} | {meters}" + (active.Length > 0 ? " | " + active : "") +
                   (_triggers != null ? " | " + _bus.Triggers : "");
        }

        if (_options.Simulate) line = $"[{DrivingSimulator.DescribePhase(Clock.Now - _simStart)}] " + line;
        return line + tail;
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

    public void Dispose() => Stop();
}
