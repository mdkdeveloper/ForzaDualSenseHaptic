using System.Net;
using System.Net.Sockets;
using ForzaHaptics.Controllers;
using ForzaHaptics.Haptics;
using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;

namespace ForzaHaptics.Tests;

public sealed class EngineLifecycleTests
{
    [Fact]
    public async Task NoControllerStillReceivesAndForwardsTelemetry()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int targetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port;
        var cfg = new AppConfig { Port = 0, ForwardTo = new List<string> { $"127.0.0.1:{targetPort}" } };
        using var engine = new HapticEngine(() => cfg, () => ControllerSnapshot.Disconnected,
            (_, _, _) => throw new InvalidOperationException("No output should be attempted"));
        Assert.True(engine.Start(new EngineOptions()));
        Assert.True(engine.IsRunning);
        Assert.Contains("Waiting for controller", engine.BuildStatus());
        byte[] packet = new DrivingSimulator().Sample(10).ToBytes();
        using var sender = new UdpClient();
        await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, engine.ListeningPort!.Value), TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await target.ReceiveAsync(timeout.Token);
        Assert.Equal(packet, received.Buffer);
        Assert.True(SpinWait.SpinUntil(() => !engine.BuildStatus().Contains("Telemetry stale"), 5000));
        engine.Stop();
        Assert.False(engine.IsRunning);
        Assert.Null(engine.ListeningPort);
    }

    [Fact]
    public void DisconnectAndTransportChangeKeepSamePortAndCreateFreshOutput()
    {
        var snapshot = Connected("bt-1", ControllerTransport.Bluetooth);
        var outputs = new List<FakeOutput>();
        var sources = new List<HapticSynth>();
        using var engine = CreateEngine(() => snapshot, (_, options, factory) =>
        {
            sources.Add(Assert.IsType<HapticSynth>(factory(options.ControllerDeviceId == "bt-1" ? 3000 : 48000)));
            var output = new FakeOutput();
            outputs.Add(output);
            return (output, options.ControllerDeviceId);
        });
        Assert.True(engine.Start(new EngineOptions { ControllerDeviceId = "bt-1" }));
        int? port = engine.ListeningPort;
        snapshot = ControllerSnapshot.Disconnected;
        engine.RefreshOutput();
        Assert.True(outputs[0].Disposed);
        Assert.Null(engine.ActiveControllerDeviceId);
        Assert.True(engine.IsRunning);
        Assert.Equal(port, engine.ListeningPort);
        snapshot = Connected("usb-2", ControllerTransport.Usb);
        engine.RefreshOutput();
        Assert.Equal("usb-2", engine.ActiveControllerDeviceId);
        Assert.True(outputs[1].IsAlive);
        Assert.NotSame(sources[0], sources[1]);
        Assert.Equal(3000, sources[0].SampleRate);
        Assert.Equal(48000, sources[1].SampleRate);
        Assert.Equal(port, engine.ListeningPort);
    }

    [Fact]
    public void SuspensionPreventsReopeningUntilResumedAndStopStaysStopped()
    {
        int attempts = 0;
        using var engine = CreateEngine(() => Connected("bt", ControllerTransport.Bluetooth), (_, options, _) =>
        {
            attempts++;
            return (new FakeOutput(), options.ControllerDeviceId);
        });
        engine.Start(new EngineOptions());
        int? port = engine.ListeningPort;
        engine.SuspendOutput();
        engine.RefreshOutput();
        Assert.Equal(1, attempts);
        Assert.True(engine.IsRunning);
        Assert.Equal(port, engine.ListeningPort);
        engine.ResumeOutput();
        Assert.Equal(2, attempts);
        engine.Stop();
        engine.ResumeOutput();
        engine.RefreshOutput();
        Assert.Equal(2, attempts);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void FailedOutputRetriesAndDeadOutputReopensWhileControllerStillPresent()
    {
        int attempts = 0;
        FakeOutput? output = null;
        using var engine = CreateEngine(() => Connected("usb", ControllerTransport.Usb), (_, options, _) =>
        {
            if (++attempts == 1) throw new IOException("Endpoint initializing");
            output = new FakeOutput();
            return (output, options.ControllerDeviceId);
        });
        Assert.True(engine.Start(new EngineOptions()));
        Assert.True(engine.IsRunning);
        int? port = engine.ListeningPort;
        engine.RefreshOutput();
        Assert.True(output!.IsAlive);
        FakeOutput previous = output;
        previous.IsAlive = false;
        engine.RefreshOutput();
        Assert.Equal(3, attempts);
        Assert.True(previous.Disposed);
        Assert.NotSame(previous, output);
        Assert.Equal(port, engine.ListeningPort);
    }

    [Fact]
    public void BusyPortFailsWithoutStartingOutputAndCanRetry()
    {
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        int attempts = 0;
        using var engine = CreateEngine(() => Connected("usb", ControllerTransport.Usb), (_, options, _) =>
        {
            attempts++;
            return (new FakeOutput(), options.ControllerDeviceId);
        });
        Assert.False(engine.Start(new EngineOptions { Port = port }));
        Assert.False(engine.IsRunning);
        Assert.Equal(0, attempts);
        Assert.Null(engine.ListeningPort);
        occupied.Dispose();
        Assert.True(engine.Start(new EngineOptions { Port = port }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task StopRacingOutputCreationClosesOutputAndPreventsResurrection()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = new FakeOutput();
        using var engine = CreateEngine(() => Connected("usb", ControllerTransport.Usb), (_, options, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            return (output, options.ControllerDeviceId);
        });
        var start = Task.Run(() => engine.Start(new EngineOptions()), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var stop = Task.Run(engine.Stop, TestContext.Current.CancellationToken);
        release.Set();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        engine.RefreshOutput();
        Assert.False(engine.IsRunning);
        Assert.True(output.Disposed);
        Assert.Null(engine.ListeningPort);
    }

    [Fact]
    public async Task TimerAttachesControllerThatAppearsAfterStartup()
    {
        ControllerSnapshot snapshot = ControllerSnapshot.Disconnected;
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = CreateEngine(() => Volatile.Read(ref snapshot), (_, options, _) =>
            (new FakeOutput { OnStart = () => attached.TrySetResult() }, options.ControllerDeviceId));
        engine.Start(new EngineOptions());
        Volatile.Write(ref snapshot, Connected("usb", ControllerTransport.Usb));
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("usb", engine.ActiveControllerDeviceId);
    }

    [Fact]
    public void OutputStartFailureDisposesPartialOutputAndRetainsListener()
    {
        var failed = new FakeOutput { OnStart = () => throw new IOException("Start failed") };
        int attempts = 0;
        using var engine = CreateEngine(() => Connected("usb", ControllerTransport.Usb), (_, options, _) =>
            (++attempts == 1 ? failed : new FakeOutput(), options.ControllerDeviceId));
        Assert.True(engine.Start(new EngineOptions()));
        Assert.True(failed.Disposed);
        Assert.True(engine.IsRunning);
        Assert.Null(engine.ActiveControllerDeviceId);
        engine.RefreshOutput();
        Assert.Equal("usb", engine.ActiveControllerDeviceId);
    }

    [Theory]
    [InlineData("usb", ControllerTransport.Bluetooth)]
    [InlineData("bt", ControllerTransport.Usb)]
    public void ExplicitOutputModeWaitsForMatchingTransport(string mode, ControllerTransport transport)
    {
        int attempts = 0;
        using var engine = CreateEngine(() => Connected("device", transport), (_, options, _) =>
        {
            attempts++;
            return (new FakeOutput(), options.ControllerDeviceId);
        });
        Assert.True(engine.Start(new EngineOptions { Output = mode }));
        engine.RefreshOutput();
        Assert.Equal(0, attempts);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task SuspendedControllerKeepsRecordingPackets()
    {
        string path = Path.Combine(Path.GetTempPath(), "forzahaptics-listener-" + Guid.NewGuid() + ".bin");
        try
        {
            using var engine = CreateEngine(() => Connected("usb", ControllerTransport.Usb), (_, options, _) =>
                (new FakeOutput(), options.ControllerDeviceId));
            engine.Start(new EngineOptions { RecordPath = path });
            engine.SuspendOutput();
            byte[] packet = new DrivingSimulator().Sample(10).ToBytes();
            using var sender = new UdpClient();
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, engine.ListeningPort!.Value), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => !engine.BuildStatus().Contains("Telemetry stale"), 5000));
            engine.Stop();
            var records = TelemetryRecording.Load(path);
            Assert.Single(records);
            Assert.Equal(packet, records[0].Data);
        }
        finally { File.Delete(path); }
    }

    private static ControllerSnapshot Connected(string id, ControllerTransport transport) => new()
    {
        IsConnected = true, DeviceId = id, Transport = transport,
    };

    private static HapticEngine CreateEngine(Func<ControllerSnapshot> snapshot,
        Func<AppConfig, EngineOptions, Func<int, IHapticSource>, (IHapticOutput?, string?)> factory) =>
        new(() => new AppConfig { Port = 0 }, snapshot, factory);

    private sealed class FakeOutput : IHapticOutput
    {
        public string Description => "Fake output";
        public string Health => "";
        public bool IsAlive { get; set; }
        public bool Disposed { get; private set; }
        public Action? OnStart { get; init; }
        public void Start() { IsAlive = true; OnStart?.Invoke(); }
        public void Dispose() { Disposed = true; IsAlive = false; }
    }
}
