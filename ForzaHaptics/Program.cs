using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ForzaHaptics.Haptics;
using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Util;
using NAudio.CoreAudioApi;

namespace ForzaHaptics;

internal sealed class Options
{
    public string? ConfigPath, RecordPath, ReplayPath, RenderPath, Output;
    public int? Port;
    public bool Simulate, Test, TriggerTest, List, Help, ConsoleMode;
    public double Seconds = DrivingSimulator.Duration;
    public bool SecondsSet;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"A value is required after {a}");

            switch (a.ToLowerInvariant())
            {
                case "--config": o.ConfigPath = Next(); break;
                case "--port":
                    o.Port = int.TryParse(Next(), out int port) && port is > 0 and < 65536
                        ? port
                        : throw new ArgumentException("Invalid port number");
                    break;
                case "--output": o.Output = Next(); break;
                case "--simulate":
                case "--sim": o.Simulate = true; break;
                case "--replay": o.ReplayPath = Next(); break;
                case "--record": o.RecordPath = Next(); break;
                case "--render": o.RenderPath = Next(); break;
                case "--seconds":
                    o.Seconds = double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0
                        ? s
                        : throw new ArgumentException("Invalid duration");
                    o.SecondsSet = true;
                    break;
                case "--test": o.Test = true; break;
                case "--trigger-test": o.TriggerTest = true; break;
                case "--list": o.List = true; break;
                case "--console": o.ConsoleMode = true; break;
                case "-h":
                case "--help":
                case "/?": o.Help = true; break;
                default: throw new ArgumentException($"Unknown option: {a}");
            }
        }

        if (o.Simulate && o.ReplayPath != null) throw new ArgumentException("--simulate and --replay cannot be used together");
        if (o.Test && (o.Simulate || o.ReplayPath != null || o.RenderPath != null))
            throw new ArgumentException("--test must be used separately from other modes");
        if (o.TriggerTest && (o.Test || o.Simulate || o.ReplayPath != null || o.RenderPath != null))
            throw new ArgumentException("--trigger-test must be used separately from other modes");
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""

        Usage: ForzaHaptics [options]

          (no options)         open the settings and profiles window
          --console            run without a window: receive FH6 telemetry and drive DualSense haptics
          --simulate           use a synthetic drive instead of the game (test without FH6)
          --test               motor test: left → right → sweep → impulses
          --trigger-test       manual 12-second gear-only trigger test; other effects stay silent
          --record FILE        record telemetry to a file in parallel (.fhrec)
          --replay FILE        replay recorded telemetry in a loop
          --render FILE.wav    offline: generate a WAV (left/right motor) without a controller;
                               source is the simulator or --replay; --seconds N sets the duration
          --list               show audio devices and DualSense HID devices
          --output auto|usb|bt select the output (defaults to the active profile)
          --port N             UDP port (defaults to the active profile)
          --config FILE        explicit settings file (otherwise use the active profile, including built-in Default)

        """);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // No options: open the window.
        if (args.Length == 0) return Gui.GuiApp.Run(args);

        ConsoleHost.Attach();

        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            Options.PrintHelp();
            return 2;
        }

        if (opt.Help)
        {
            Options.PrintHelp();
            return 0;
        }

        Log.Info("ForzaHaptics — Forza Horizon 6 telemetry → DualSense haptics");

        if (opt.List)
        {
            Devices.PrintAll();
            return 0;
        }

        ConfigManager config;
        try
        {
            config = opt.ConfigPath != null
                ? ConfigManager.Open(opt.ConfigPath, createIfMissing: false)
                : ProfileStore.Open().OpenActive(out _);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load configuration: {ex.Message}");
            return 1;
        }

        using (config)
        {
            try
            {
                return opt.RenderPath != null ? OfflineRender.Run(opt, config) : LiveRun.Run(opt, config);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.EndStatus();
                Log.Error($"Error: {ex.Message}");
                return 1;
            }
        }
    }

}

/// <summary>The app is built as a windowed executable, so console modes attach to a terminal.</summary>
internal static class ConsoleHost
{
    public static void Attach()
    {
        var utf8 = new UTF8Encoding(false);
        // Output is already redirected (pipe or file), so no console is needed. Set the encoding explicitly
        // because a windowed executable cannot change the pipe's code page.
        if (GetFileType(GetStdHandle(StdOutputHandle)) is FileTypeDisk or FileTypePipe)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            return;
        }
        if (!AttachConsole(AttachParentProcess)) AllocConsole();
        try { Console.OutputEncoding = utf8; } catch { /* Older consoles. */ }
    }

    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;
    private const uint FileTypeDisk = 1, FileTypePipe = 3;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
}

/// <summary>Console mode: run the engine and print status until Ctrl+C.</summary>
internal static class LiveRun
{
    public static int Run(Options opt, ConfigManager config)
    {
        using var engine = new HapticEngine(() => config.Current, () => config.Revision);
        var options = new EngineOptions
        {
            Simulate = opt.Simulate,
            Test = opt.Test,
            TriggerTest = opt.TriggerTest,
            ReplayPath = opt.ReplayPath,
            RecordPath = opt.RecordPath,
            Port = opt.Port,
            Output = opt.Output,
        };
        if (!engine.Start(options)) return 1;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Log.Info(config.IsReadOnly
            ? "Running with built-in Default (read-only). Press Ctrl+C to exit."
            : "Running. Press Ctrl+C to exit. The profile file can be edited live.");
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Log.Status(engine.BuildStatus());
                cts.Token.WaitHandle.WaitOne(200);
            }
        }
        finally
        {
            Log.EndStatus();
            engine.Stop();
        }
        return 0;
    }
}

internal static class OutputFactory
{
    public static IHapticOutput? Create(string mode, AppConfig cfg, Func<int, IHapticSource> factory, out string? activeDeviceId, string? controllerDeviceId = null, bool quiet = false, Action<string>? reportIssue = null)
    {
        void Error(string message) { reportIssue?.Invoke(message); if (!quiet) Log.Error(message); }
        void Warn(string message) { reportIssue?.Invoke(message); if (!quiet) Log.Warn(message); }
        activeDeviceId = null;
        if (mode is not ("auto" or "usb" or "bt"))
        {
            Error($"Unknown output mode '{mode}' (auto | usb | bt)");
            return null;
        }

        var hid = DualSenseHid.Find().OrderBy(h => h.Device.DevicePath, StringComparer.Ordinal).ToList();
        var selected = controllerDeviceId == null ? null : hid.FirstOrDefault(h => h.Device.DevicePath == controllerDeviceId);
        if (controllerDeviceId != null)
        {
            if (selected == null)
            {
                Error("The selected controller is no longer connected.");
                return null;
            }
            if ((mode == "usb" && selected.IsBluetooth) || (mode == "bt" && !selected.IsBluetooth))
            {
                Error("The selected controller does not match the requested output mode. Wait for controller status to refresh.");
                return null;
            }
            mode = selected.IsBluetooth ? "bt" : "usb";
        }

        if (mode is "auto" or "usb")
        {
            var usbControllers = hid.Where(h => !h.IsBluetooth).ToList();
            if (usbControllers.Count > 1)
            {
                Error("Connect only one USB DualSense to ensure audio and controller status refer to the same device.");
                return null;
            }
            MMDevice? device = null;
            try
            {
                if (usbControllers.Count == 1) device = UsbAudioOutput.FindDualSenseEndpoint();
            }
            catch (Exception ex)
            {
                Warn($"Failed to enumerate audio devices: {ex.Message}");
            }

            if (device != null)
            {
                try
                {
                    var output = new UsbAudioOutput(device, cfg.UsbLatencyMs, cfg.UsbHapticChannels[0], cfg.UsbHapticChannels[1], factory);
                    activeDeviceId = usbControllers[0].Device.DevicePath;
                    return output;
                }
                catch (Exception ex)
                {
                    device.Dispose();
                    Error($"DualSense USB audio: {ex.Message}");
                    if (mode == "usb") return null;
                }
            }
            else if (mode == "usb")
            {
                Error("The DualSense audio device was not found. Connect the controller over USB (check with --list).");
                return null;
            }
        }

        var bt = hid.FirstOrDefault(h => h.IsBluetooth && (controllerDeviceId == null || h.Device.DevicePath == controllerDeviceId));
        if (bt != null)
        {
            try
            {
                var output = new BluetoothHidOutput(bt.Device, factory);
                activeDeviceId = bt.Device.DevicePath;
                return output;
            }
            catch (Exception ex)
            {
                Error($"Bluetooth HID: {ex.Message}");
                return null;
            }
        }

        if (mode == "bt")
            Error("No DualSense controller was found over Bluetooth.");
        else if (hid.Count > 0)
            Error("A DualSense controller is connected over USB, but its four-channel audio device is unavailable. " +
                      "Make sure Quadraphonic is selected in the DualSense sound settings (see README).");
        else
            Error("No DualSense controller was found over USB or Bluetooth. Run with --list to inspect devices.");
        return null;
    }
}

/// <summary>Connect adaptive triggers. Failure does not stop haptic output.</summary>
internal static class TriggerOutput
{
    public static TriggerHidWriter? Create(AppConfig cfg, HapticBus bus, bool preferBluetooth, string? controllerDeviceId = null, Action<string>? reportIssue = null)
    {
        if (!cfg.Triggers.Enabled) return null;
        try
        {
            var hid = DualSenseHid.Find();
            var info = controllerDeviceId != null
                ? hid.FirstOrDefault(h => h.Device.DevicePath == controllerDeviceId && h.IsBluetooth == preferBluetooth)
                : hid.FirstOrDefault(h => h.IsBluetooth == preferBluetooth);
            if (info == null)
            {
                (reportIssue ?? Log.Warn)("Triggers: no DualSense HID device was found; adaptive triggers are disabled.");
                return null;
            }
            return new TriggerHidWriter(info, bus);
        }
        catch (Exception ex)
        {
            (reportIssue ?? Log.Warn)($"Triggers: {ex.Message}; adaptive triggers are disabled, but haptics remain active.");
            return null;
        }
    }
}

internal static class Devices
{
    public static void PrintAll()
    {
        Log.Info("Audio outputs (WASAPI):");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name = UsbAudioOutput.SafeName(device);
                string mark = UsbAudioOutput.IsDualSenseName(name) ? "   ← DualSense" : "";
                Log.Info($"  • {name} — {UsbAudioOutput.DescribeFormat(device)}{mark}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"  failed: {ex.Message}");
        }

        Log.Info("DualSense HID devices:");
        try
        {
            var hid = DualSenseHid.Find();
            if (hid.Count == 0) Log.Info("  (none found)");
            foreach (var h in hid)
                Log.Info($"  • {h.Name} — {(h.IsBluetooth ? "Bluetooth" : "USB")}, input {h.MaxInput} B, output {h.MaxOutput} B");
        }
        catch (Exception ex)
        {
            Log.Warn($"  failed: {ex.Message}");
        }
    }
}

/// <summary>Offline WAV rendering for testing and tuning effects without a controller.</summary>
internal static class OfflineRender
{
    private const int SampleRate = 48000;
    private const int Block = 480; // 10 ms, matching the audio stream.

    public static int Run(Options opt, ConfigManager config)
    {
        var bus = new HapticBus();
        var processor = new TelemetryProcessor(bus, () => config.Current, () => config.Revision);
        var synth = new HapticSynth(bus, () => config.Current, SampleRate);
        string path = Path.GetFullPath(opt.RenderPath!);
        var lastTriggers = bus.Triggers;
        int triggerChanges = 0, triggerModeSwitches = 0;

        void Process(ForzaPacket packet, double t)
        {
            processor.Process(packet, t);
            var now = bus.Triggers;
            if (now != lastTriggers) triggerChanges++;
            if (now.L2.Mode != lastTriggers.L2.Mode || now.R2.Mode != lastTriggers.R2.Mode) triggerModeSwitches++;
            lastTriggers = now;
        }

        var left = new float[Block];
        var right = new float[Block];
        long framesDone = 0;
        var perSecond = new List<(double L, double R)>();
        double secSumL = 0, secSumR = 0;
        int secFrames = 0;

        using (var wav = new WavWriter(path, SampleRate))
        {
            void RenderUntil(double time)
            {
                long target = (long)Math.Round(time * SampleRate);
                while (framesDone < target)
                {
                    int n = (int)Math.Min(Block, target - framesDone);
                    processor.CheckTimeout(framesDone / (double)SampleRate);
                    synth.Render(left.AsSpan(0, n), right.AsSpan(0, n));
                    wav.Write(left.AsSpan(0, n), right.AsSpan(0, n));
                    for (int i = 0; i < n; i++)
                    {
                        secSumL += left[i] * left[i];
                        secSumR += right[i] * right[i];
                        if (++secFrames == SampleRate)
                        {
                            perSecond.Add((Math.Sqrt(secSumL / SampleRate), Math.Sqrt(secSumR / SampleRate)));
                            secSumL = secSumR = 0;
                            secFrames = 0;
                        }
                    }
                    framesDone += n;
                }
            }

            if (opt.ReplayPath != null)
            {
                var records = TelemetryRecording.Load(opt.ReplayPath);
                if (records.Count == 0)
                {
                    Log.Error("The recording file is empty");
                    return 1;
                }
                double t0 = records[0].Time;
                double last = 0;
                foreach (var (time, data) in records)
                {
                    double t = time - t0;
                    if (opt.SecondsSet && t > opt.Seconds) break;
                    RenderUntil(t);
                    if (ForzaPacket.TryParse(data, out var packet)) Process(packet, t);
                    processor.CheckTimeout(t);
                    last = t;
                }
                RenderUntil(last + 0.5);
            }
            else
            {
                var sim = new DrivingSimulator();
                const double step = 1.0 / 60.0;
                for (double t = 0; t < opt.Seconds; t += step)
                {
                    byte[] bytes = sim.Sample(t).ToBytes(); // Round-trip through bytes to exercise the parser too.
                    if (ForzaPacket.TryParse(bytes, out var packet)) Process(packet, t);
                    RenderUntil(t + step);
                }
            }

            Log.Ok($"WAV: {path}");
            Log.Info($"  duration {wav.Seconds:0.0} s, peak L {wav.PeakL:0.00} / R {wav.PeakR:0.00}, RMS L {wav.RmsL:0.000} / R {wav.RmsR:0.000}");
        }

        var c = processor.Counters;
        Log.Info($"  packets {c.Packets}; suspension hits {c.Bumps}, shifts {c.Shifts}, impacts {c.Impacts}, " +
                 $"smashes {c.Smashes}, splashes {c.Splashes}");
        Log.Info($"  L2/R2 desired state changes: {triggerChanges}, including {triggerModeSwitches} mode changes (hardware writes are separately rate-limited)");
        Log.Info("  RMS by second (L | R):");
        for (int i = 0; i < perSecond.Count; i++)
        {
            var (l, r) = perSecond[i];
            string phase = opt.ReplayPath == null ? DrivingSimulator.DescribePhase(i + 0.5) : "";
            Log.Info($"  {i,3} s  {Bar(l)} {l:0.000} | {Bar(r)} {r:0.000}  {phase}");
        }
        return 0;
    }

    private static string Bar(double rms)
    {
        int n = (int)Math.Round(Math.Min(1.0, rms / 0.5) * 20);
        return new string('█', n).PadRight(20, '·');
    }
}
