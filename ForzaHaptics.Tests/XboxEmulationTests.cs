using ForzaHaptics.Controllers;
using ForzaHaptics.Emulation;

namespace ForzaHaptics.Tests;

public sealed class XboxEmulationTests
{
    [Fact]
    public async Task ActivationOrdersWhitelistInputVirtualDeviceAndHide()
    {
        using var f = new Fixture();
        await f.Service.InitializeAsync();
        await f.Service.SetEnabledAsync(true);
        Assert.True(f.Service.IsEnabled);
        Assert.Null(f.Factory.Last);
        f.Connect();
        f.Service.Tick();
        Assert.Equal("Connected", f.Service.Status);
        Assert.True(f.Events.IndexOf("allow") < f.Events.IndexOf("create"));
        Assert.True(f.Events.IndexOf("submit") < f.Events.IndexOf("hide"));
        await f.Service.SetEnabledAsync(false);
        Assert.False(f.Service.IsEnabled);
        Assert.True(f.Factory.Last!.Disposed);
        Assert.Equal("restore", f.Events.Last());
    }

    [Fact]
    public async Task ReportsMissingDependenciesTogetherWithoutHiding()
    {
        using var f = new Fixture();
        f.Hide.CheckError = "HidHide missing";
        f.Factory.CheckError = "ViGEmBus missing";
        await f.Service.SetEnabledAsync(true);
        Assert.Contains("HidHide missing", f.Service.Error);
        Assert.Contains("ViGEmBus missing", f.Service.Error);
        Assert.DoesNotContain("allow", f.Events);
        Assert.DoesNotContain("create", f.Events);
        f.Hide.CheckError = f.Factory.CheckError = null;
        f.Connect();
        await f.Service.CheckAgainAsync();
        Assert.Null(f.Service.Error);
        Assert.Equal("Connected", f.Service.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreationOrHidingFailureRollsBack(bool failCreate)
    {
        using var f = new Fixture();
        f.Connect();
        f.Factory.CreateError = failCreate;
        f.Hide.HideError = !failCreate;
        await f.Service.SetEnabledAsync(true);
        Assert.NotNull(f.Service.Error);
        Assert.Equal("restore", f.Events.Last());
        if (!failCreate) Assert.True(f.Factory.Last!.Disposed);
    }

    [Fact]
    public async Task RecoveryMustSucceedBeforeActivation()
    {
        using var f = new Fixture();
        f.Hide.RecoveryError = true;
        await f.Service.InitializeAsync();
        await f.Service.SetEnabledAsync(true);
        Assert.NotNull(f.Service.Error);
        Assert.DoesNotContain("allow", f.Events);
        f.Hide.RecoveryError = false;
        f.Connect();
        await f.Service.CheckAgainAsync();
        Assert.Equal("Connected", f.Service.Status);
    }

    [Fact]
    public async Task ProcessLeaseFailureNeverChangesHidHide()
    {
        using var f = new Fixture(denyLease: true);
        await f.Service.SetEnabledAsync(true);
        Assert.Contains("Another instance", f.Service.Error);
        Assert.Empty(f.Events);
        f.Service.Dispose();
        Assert.False(f.Hide.Disposed);
        Assert.Empty(f.Events);
    }

    [Fact]
    public async Task StaleInputNeutralizesAndCannotQueueRumble()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(150, 80);
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        f.Now += 301;
        f.Service.Tick();
        Assert.True(f.Rumbles[0].Disposed);
        Assert.Equal((ushort)0, f.Factory.Last.Inputs.Last().Buttons);
        f.Factory.Last.Feedback(255, 255);
        f.Connect();
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        Assert.Equal("Connected", f.Service.Status);
    }

    [Fact]
    public async Task TelemetryPriorityQuiescesImmediatelyAndDiscardsOldFeedback()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(120, 70);
        f.Service.Tick();
        f.Service.SetHapticsActive(true);
        Assert.True(f.Rumbles.Single().Disposed);
        f.Factory.Last.Feedback(250, 240);
        f.Service.Tick();
        Assert.Equal("Connected — Forza haptics priority", f.Service.Status);
        f.Service.SetHapticsActive(false);
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        f.Factory.Last.Feedback(60, 40);
        f.Service.Tick();
        Assert.Equal((60, 40), f.Rumbles[1].Writes.Single());
    }

    [Fact]
    public async Task EnabledTelemetryWithoutAnyPacketsStillSuppressesFeedback()
    {
        using var f = new Fixture();
        f.Service.SetHapticsActive(true);
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(255, 255);
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        Assert.True(f.Service.IsEnabled);
    }

    [Fact]
    public async Task DisconnectReconnectAndDeviceSwitchNeverKeepTwoXboxTargets()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var first = f.Factory.Last!;
        f.Controller.State = ControllerSnapshot.Disconnected;
        f.Service.Tick();
        Assert.True(first.Disposed);
        Assert.Equal("Waiting for controller", f.Service.Status);
        f.Connect("bt");
        f.Service.Tick();
        var second = f.Factory.Last!;
        Assert.NotSame(first, second);
        f.Connect("usb");
        f.Service.Tick();
        Assert.True(second.Disposed);
        Assert.Equal(3, f.Factory.Created);
    }

    [Fact]
    public async Task SuspendPreventsReconnectionUntilResume()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        await f.Service.SuspendAsync();
        f.Service.Tick();
        Assert.Equal(1, f.Factory.Created);
        Assert.True(f.Factory.Last!.Disposed);
        f.Service.Resume();
        f.Service.Tick();
        Assert.Equal(2, f.Factory.Created);
        Assert.True(f.Service.IsEnabled);
    }

    [Fact]
    public async Task RepeatedEnableDoesNotCreateDuplicateTargetAndShutdownCleansUp()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        await f.Service.SetEnabledAsync(true);
        Assert.Equal(1, f.Factory.Created);
        f.Factory.Last!.Feedback(100, 200);
        f.Service.Tick();
        f.Service.Dispose();
        Assert.True(f.Rumbles.Single().Disposed);
        Assert.True(f.Factory.Last.Disposed);
        Assert.True(f.Lease.Disposed);
        Assert.Contains("restore", f.Events);
    }

    [Fact]
    public async Task RumbleResetFailureBlocksTelemetryHandoffButStillCleansUp()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(100, 100);
        f.Service.Tick();
        f.Rumbles.Single().FailDispose = true;
        Assert.Throws<IOException>(() => f.Service.SetHapticsActive(true));
        f.Factory.Last.Feedback(200, 200);
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        f.Service.SetHapticsActive(true);
        Assert.Equal(2, f.Rumbles.Count);
        Assert.True(f.Rumbles[1].Disposed);
        Assert.Empty(f.Rumbles[1].Writes);
        await f.Service.SetEnabledAsync(false);
        Assert.True(f.Factory.Last.Disposed);
        Assert.False(f.Service.IsEnabled);
    }

    private sealed class Fixture : IDisposable
    {
        public long Now = 1000;
        public List<string> Events { get; } = [];
        public FakeController Controller { get; } = new();
        public FakeHide Hide { get; }
        public FakeFactory Factory { get; }
        public List<FakeRumble> Rumbles { get; } = [];
        public Lease Lease { get; } = new();
        public XboxEmulationService Service { get; }
        public Fixture(bool denyLease = false)
        {
            Hide = new(Events);
            Factory = new(Events);
            Service = new(Controller, Hide, Factory, _ =>
            {
                var output = new FakeRumble(); Rumbles.Add(output); return output;
            }, () => denyLease ? throw new IOException("Another instance") : Lease, () => Now, runTimer: false);
        }
        public void Connect(string id = "usb")
        {
            Controller.State = new() { DeviceId = id, IsConnected = true, Transport = id == "bt" ? ControllerTransport.Bluetooth : ControllerTransport.Usb, LastInputTick = Now };
            Controller.Emit(new(id, Now, 10, -20, 30, -40, 50, 60, 0x1000));
        }
        public void Dispose() => Service.Dispose();
    }

    private sealed class FakeController : IControllerService
    {
        public ControllerSnapshot State = ControllerSnapshot.Disconnected;
        public ControllerSnapshot Snapshot => State;
        public event Action<ControllerInputState>? InputReceived;
        public void Emit(ControllerInputState input) => InputReceived?.Invoke(input);
        public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Lease : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeHide(List<string> events) : IHidHideService
    {
        public string? CheckError;
        public bool HideError, RecoveryError, Disposed;
        public void CheckAvailable() { if (CheckError != null) throw new IOException(CheckError); }
        public void Recover() { events.Add("recover"); if (RecoveryError) throw new IOException("Recovery failed"); }
        public void AllowApplication() => events.Add("allow");
        public void Hide(string path) { events.Add("hide"); if (HideError) throw new IOException("Hide failed"); }
        public void Restore() => events.Add("restore");
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeFactory(List<string> events) : IVirtualXboxFactory
    {
        public string? CheckError;
        public bool CreateError;
        public FakeXbox? Last;
        public int Created;
        public void CheckAvailable() { if (CheckError != null) throw new IOException(CheckError); }
        public IVirtualXbox Create()
        {
            events.Add("create");
            if (CreateError) throw new IOException("Create failed");
            Created++;
            return Last = new FakeXbox(events);
        }
    }
    private sealed class FakeXbox(List<string> events) : IVirtualXbox
    {
        public event Action<byte, byte>? RumbleReceived;
        public List<ControllerInputState> Inputs { get; } = [];
        public bool Disposed;
        public void Feedback(byte large, byte small) => RumbleReceived?.Invoke(large, small);
        public void Submit(ControllerInputState input) { events.Add("submit"); Inputs.Add(input); }
        public void Dispose() { Disposed = true; events.Add("dispose xbox"); }
    }
    private sealed class FakeRumble : IXboxRumbleOutput
    {
        public List<(int Large, int Small)> Writes { get; } = [];
        public bool Disposed, FailDispose;
        public void Write(byte large, byte small) => Writes.Add((large, small));
        public void Dispose() { Disposed = true; if (FailDispose) throw new IOException("Reset failed"); }
    }
}
