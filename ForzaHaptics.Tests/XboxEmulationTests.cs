using ForzaHaptics.Controllers;
using ForzaHaptics.Emulation;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class XboxEmulationTests
{
    [Fact]
    public async Task CallbackCapturedBeforeStopCannotAffectReplacementDevice()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var staleCallback = f.Factory.Last!.CaptureFeedback();
        await f.Service.SetEnabledAsync(false);
        await f.Service.SetEnabledAsync(true);
        staleCallback!(XboxFeedback.FromViGEm(200, 200) with { LeftTrigger = 1 });
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        f.Factory.Last!.Feedback(100, 100);
        f.Service.Tick();
        Assert.Single(f.Rumbles);
    }

    [Fact]
    public async Task SlowCreateDoesNotBlockPriorityAndDuplicateRequestsAreNotQueued()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Connect();
        f.Factory.OnCreate = () => { entered.Set(); Assert.True(release.Wait(5000, TestContext.Current.CancellationToken)); };
        var starting = f.Service.SetEnabledAsync(true);
        try
        {
            Assert.True(entered.Wait(5000, TestContext.Current.CancellationToken));
            Assert.Equal(XboxEmulationState.Starting, f.Service.State);
            Assert.True(f.Service.IsBusy);
            await Task.Run(() => f.Service.SetHapticsActive(true), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await f.Service.SetEnabledAsync(false).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await f.Service.SetEnabledAsync(true).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        finally { release.Set(); }
        await starting;
        Assert.Equal(1, f.Factory.Created);
        Assert.Equal(XboxEmulationState.Running, f.Service.State);
    }

    [Fact]
    public async Task SlowRemovalRestoresPhysicalControllerBeforeSdkReturns()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var xbox = f.Factory.Last!;
        xbox.Feedback(100, 100);
        f.Service.Tick();
        xbox.OnDispose = () => { entered.Set(); Assert.True(release.Wait(5000, TestContext.Current.CancellationToken)); };
        var stopping = f.Service.SetEnabledAsync(false);
        try
        {
            Assert.True(entered.Wait(5000, TestContext.Current.CancellationToken));
            Assert.Equal(XboxEmulationState.Stopping, f.Service.State);
            Assert.True(f.Service.IsBusy);
            Assert.Contains("restore", f.Events);
            Assert.True(f.Rumbles.Single().Disposed);
            Assert.Equal((ushort)0, xbox.Inputs.Last().Buttons);
            await Task.Run(() => f.Service.SetHapticsActive(true), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await f.Service.SetEnabledAsync(true).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, f.Factory.Created);
        }
        finally { release.Set(); }
        await stopping;
        Assert.Equal(XboxEmulationState.Off, f.Service.State);
        Assert.False(f.Service.IsEnabled);
        Assert.Equal(1, f.Factory.Created);
    }

    [Fact]
    public async Task ShutdownDuringCreationWaitsWithoutDeadlockAndNeverHidesController()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Connect();
        f.Factory.OnCreate = () => { entered.Set(); Assert.True(release.Wait(5000, TestContext.Current.CancellationToken)); };
        var starting = f.Service.SetEnabledAsync(true);
        Assert.True(entered.Wait(5000, TestContext.Current.CancellationToken));
        var shutdown = Task.Run(f.Service.Dispose, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => !f.Service.IsEnabled, 2000));
        }
        finally { release.Set(); }
        await Task.WhenAll(starting, shutdown).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(f.Factory.Last!.Disposed);
        Assert.DoesNotContain("hide", f.Events);
        Assert.True(f.Lease.Disposed);
    }

    [Fact]
    public async Task RemovalRecoveryVerifiesOnlyAndRequiresExplicitNewEnable()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.OnDispose = () => { f.Factory.PendingRemoval = true; throw new IOException("Windows removal incomplete"); };
        await f.Service.SetEnabledAsync(false);
        Assert.Equal(XboxEmulationState.Failed, f.Service.State);
        await f.Service.SetEnabledAsync(true);
        Assert.Equal(1, f.Factory.Created);
        Assert.Equal(XboxEmulationState.Failed, f.Service.State);
        f.Factory.AllowRemovalCompletion = true;
        await f.Service.CheckAgainAsync();
        Assert.Equal(XboxEmulationState.Off, f.Service.State);
        Assert.False(f.Service.IsEnabled);
        Assert.Equal(1, f.Factory.Created);
        await f.Service.SetEnabledAsync(true);
        Assert.Equal(2, f.Factory.Created);
    }

    [Fact]
    public async Task CapturedHidMaestroPacketReachesRumbleAndAdaptiveTriggers()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var packet = XboxFeedbackDecoder.Decode(HIDMaestro.HMOutputSource.HidOutput,
            0x0F, Convert.FromHexString("0020644EFF00EB"), 1, f.Now, DateTimeOffset.UtcNow);
        f.Factory.Last!.Feedback(packet);
        f.Service.Tick();
        var rumble = Assert.Single(f.Rumbles);
        Assert.Equal((255, 199), Assert.Single(rumble.Writes));
        var triggers = Assert.Single(rumble.TriggerWrites)!;
        Assert.Equal(TriggerEffect.Off, triggers.L2);
        Assert.Equal(TriggerEffect.Vibration(15, 3, 1), triggers.R2);
        f.Factory.Last.Feedback(XboxFeedbackDecoder.Decode(HIDMaestro.HMOutputSource.HidOutput,
            0x0F, Convert.FromHexString("00000000FF00EB"), 2, f.Now, DateTimeOffset.UtcNow));
        f.Service.Tick();
        Assert.Equal((0, 0), rumble.Writes.Last());
        Assert.Equal(TriggerPair.Off, rumble.TriggerWrites.Last());
    }

    [Fact]
    public async Task SlowCreationUsesCurrentPhysicalInputInsteadOfStaleSnapshot()
    {
        using var f = new Fixture();
        f.Connect();
        f.Factory.OnCreate = () => { f.Now += 2000; f.Connect(); };
        await f.Service.SetEnabledAsync(true);
        Assert.Equal("Connected", f.Service.Status);
        Assert.NotEmpty(f.Factory.Last!.Inputs);
        Assert.All(f.Factory.Last.Inputs, input => Assert.Equal(f.Now, input.Timestamp));
        Assert.All(f.Factory.Last.Inputs, input => Assert.Equal((byte)50, input.LT));
    }

    [Fact]
    public async Task DisconnectDuringCreationDoesNotHideOrSubmitObsoleteInput()
    {
        using var f = new Fixture();
        f.Connect();
        f.Factory.OnCreate = () => f.Controller.State = ControllerSnapshot.Disconnected;
        await f.Service.SetEnabledAsync(true);
        Assert.True(f.Factory.Last!.Disposed);
        Assert.All(f.Factory.Last.Inputs, input => Assert.Equal((ushort)0, input.Buttons));
        Assert.DoesNotContain("hide", f.Events);
        Assert.Equal("Waiting for controller", f.Service.Status);
    }

    [Fact]
    public async Task TimedImpulseExpiresWithoutCallbackAndTelemetryCannotReplayIt()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var packet = XboxFeedback.FromViGEm(100, 50) with
        {
            LeftTrigger = 1, RightTrigger = .5f, Timestamp = f.Now,
            IsTimed = true, DurationMs = 40, DelayMs = 0, RepeatCount = 0
        };
        f.Factory.Last!.Feedback(packet);
        f.Service.Tick();
        Assert.Equal(8, f.Rumbles.Single().TriggerWrites.Single()!.L2.Strength);
        f.Now += 40;
        f.Service.Tick();
        Assert.Equal(TriggerPair.Off, f.Rumbles.Single().TriggerWrites.Last());
        f.Service.SetHapticsActive(true);
        f.Factory.Last.Feedback(packet with { Timestamp = f.Now });
        f.Service.Tick();
        f.Service.SetHapticsActive(false);
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        f.Factory.Last.Feedback(packet with { Timestamp = f.Now });
        f.Service.Tick();
        Assert.Equal(2, f.Rumbles.Count);
        Assert.Equal(8, f.Rumbles.Last().TriggerWrites.Single()!.L2.Strength);
    }

    [Fact]
    public async Task BackendChangeWhileEnabledIsRejectedWithoutDestroyingDevice()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        await f.Service.SetBackendAsync(XboxBackend.HidMaestro);
        Assert.Equal(XboxBackend.ViGEm, f.Service.Backend);
        Assert.True(f.Service.IsEnabled);
        Assert.False(f.Factory.Last!.Disposed);
        f.Factory.Last.Feedback(100, 50);
        f.Service.Tick();
        Assert.Single(f.Rumbles.Single().Writes);
    }

    [Fact]
    public async Task InvalidFeedbackIsIgnoredAndStaleInputReleasesImpulseOutput()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(XboxFeedback.FromViGEm(0, 0) with { IsValid = false });
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        f.Factory.Last.Feedback(XboxFeedback.FromViGEm(0, 0) with { LeftTrigger = 1, RightTrigger = 0 });
        f.Service.Tick();
        Assert.Equal(8, f.Rumbles.Single().TriggerWrites.Single()!.L2.Strength);
        f.Now += 301;
        f.Service.Tick();
        Assert.True(f.Rumbles.Single().Disposed);
        f.Connect();
        f.Service.Tick();
        Assert.Single(f.Rumbles);
    }

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
        Assert.True(f.Events.LastIndexOf("restore") < f.Events.LastIndexOf("dispose xbox"));
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
        Assert.Contains("restore", f.Events);
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

    [Theory]
    [InlineData(XboxBackend.ViGEm)]
    [InlineData(XboxBackend.HidMaestro)]
    public async Task TelemetryPriorityQuiescesImmediatelyAndDiscardsOldFeedback(XboxBackend backend)
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var packet = FeedbackFor(backend, f.Now);
        f.Factory.Last!.Feedback(packet);
        f.Service.Tick();
        var previous = Assert.Single(f.Rumbles);
        AssertFeedbackOutput(previous, packet);
        f.Factory.Last.Feedback(packet); // Pending feedback must also be discarded at handoff.
        f.Service.SetHapticsActive(true);
        Assert.True(previous.Disposed);
        f.Factory.Last.Feedback(packet);
        f.Service.Tick();
        Assert.Equal("Connected — Forza haptics priority", f.Service.Status);
        f.Service.SetHapticsActive(false);
        f.Now += 100;
        f.Service.Tick();
        Assert.Single(f.Rumbles);
        Assert.Single(previous.Writes);
        f.Factory.Last.Feedback(packet with { Timestamp = f.Now });
        f.Service.Tick();
        Assert.Equal(2, f.Rumbles.Count);
        AssertFeedbackOutput(f.Rumbles[1], packet);
    }

    [Theory]
    [InlineData(XboxBackend.ViGEm)]
    [InlineData(XboxBackend.HidMaestro)]
    public async Task EnabledTelemetryWithoutAnyPacketsStillSuppressesFeedback(XboxBackend backend)
    {
        using var f = new Fixture();
        f.Service.SetHapticsActive(true);
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        for (int i = 0; i < 3; i++)
        {
            f.Now += 1000;
            f.Connect(); // Controller input stays fresh while no telemetry packets arrive.
            f.Factory.Last!.Feedback(FeedbackFor(backend, f.Now));
            f.Service.Tick();
            Assert.Empty(f.Rumbles);
            Assert.Equal("Connected — Forza haptics priority", f.Service.Status);
        }
        Assert.True(f.Service.IsEnabled);
        f.Service.SetHapticsActive(false);
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        var packet = FeedbackFor(backend, f.Now);
        f.Factory.Last!.Feedback(packet);
        f.Service.Tick();
        AssertFeedbackOutput(Assert.Single(f.Rumbles), packet);
    }

    [Theory]
    [InlineData(XboxBackend.ViGEm)]
    [InlineData(XboxBackend.HidMaestro)]
    public async Task TelemetryPrioritySurvivesControllerReconnectAndEmulationRestart(XboxBackend backend)
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var staleCallback = f.Factory.Last!.CaptureFeedback();
        f.Service.SetHapticsActive(true);
        f.Controller.State = ControllerSnapshot.Disconnected;
        f.Service.Tick();
        f.Connect("bt");
        f.Service.Tick();
        f.Factory.Last!.Feedback(FeedbackFor(backend, f.Now));
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        await f.Service.SetEnabledAsync(false);
        await f.Service.SetEnabledAsync(true);
        f.Factory.Last!.Feedback(FeedbackFor(backend, f.Now));
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        f.Service.SetHapticsActive(false);
        staleCallback!(FeedbackFor(backend, f.Now));
        f.Service.Tick();
        Assert.Empty(f.Rumbles);
        var packet = FeedbackFor(backend, f.Now);
        f.Factory.Last.Feedback(packet);
        f.Service.Tick();
        AssertFeedbackOutput(Assert.Single(f.Rumbles), packet);
    }

    [Fact]
    public async Task TelemetryHandoffClearsScheduledHidMaestroImpulseBeforeItsDelayExpires()
    {
        using var f = new Fixture();
        f.Connect();
        await f.Service.SetEnabledAsync(true);
        var packet = XboxFeedbackDecoder.Decode(HIDMaestro.HMOutputSource.HidOutput,
            0x0F, Convert.FromHexString("64326432041402"), 1, f.Now, DateTimeOffset.UtcNow);
        f.Factory.Last!.Feedback(packet);
        f.Service.Tick();
        var previous = Assert.Single(f.Rumbles);
        Assert.Equal((0, 0), Assert.Single(previous.Writes));
        Assert.Equal(TriggerPair.Off, Assert.Single(previous.TriggerWrites));
        f.Service.SetHapticsActive(true);
        f.Service.SetHapticsActive(false);
        f.Now += 210; // Within the cleared packet's first active interval.
        f.Service.Tick();
        Assert.True(previous.Disposed);
        Assert.Single(f.Rumbles);
        Assert.Single(previous.Writes);
        var freshPacket = FeedbackFor(XboxBackend.HidMaestro, f.Now);
        f.Factory.Last.Feedback(freshPacket);
        f.Service.Tick();
        Assert.Equal(2, f.Rumbles.Count);
        AssertFeedbackOutput(f.Rumbles[1], freshPacket);
    }

    private static XboxFeedback FeedbackFor(XboxBackend backend, long timestamp)
        => backend == XboxBackend.ViGEm
            ? XboxFeedback.FromViGEm(120, 70) with { Timestamp = timestamp }
            : XboxFeedbackDecoder.Decode(HIDMaestro.HMOutputSource.HidOutput,
                0x0F, Convert.FromHexString("64326432FF0002"), 1, timestamp, DateTimeOffset.UtcNow);

    private static void AssertFeedbackOutput(FakeRumble output, XboxFeedback packet)
    {
        Assert.Equal(((int)packet.Large, (int)packet.Small), Assert.Single(output.Writes));
        var triggers = Assert.Single(output.TriggerWrites);
        if (packet.LeftTrigger == null)
            Assert.Null(triggers);
        else
            Assert.Equal(new TriggerPair(TriggerEffect.Vibration(15, 8, 1),
                TriggerEffect.Vibration(15, 4, 1)), triggers);
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
    private sealed class FakeFactory(List<string> events) : IVirtualXboxFactory, IVirtualXboxLifecycleFactory
    {
        public string? CheckError;
        public bool CreateError;
        public Action? OnCreate;
        public FakeXbox? Last;
        public int Created;
        public bool PendingRemoval, AllowRemovalCompletion;
        public bool HasPendingRemoval => PendingRemoval;
        public void SetPhaseCallback(Action<string> phase) { }
        public void VerifyPreviousRemoval()
        {
            if (!PendingRemoval) return;
            if (!AllowRemovalCompletion) throw new IOException("Previous removal is incomplete");
            PendingRemoval = false;
        }
        public void CheckAvailable() { if (CheckError != null) throw new IOException(CheckError); }
        public IVirtualXbox Create()
        {
            events.Add("create");
            if (CreateError) throw new IOException("Create failed");
            Created++;
            OnCreate?.Invoke();
            return Last = new FakeXbox(events);
        }
    }
    private sealed class FakeXbox(List<string> events) : IVirtualXbox
    {
        public event Action<XboxFeedback>? FeedbackReceived;
        public Action<XboxFeedback>? CaptureFeedback() => FeedbackReceived;
        public List<ControllerInputState> Inputs { get; } = [];
        public bool Disposed;
        public Action? OnDispose;
        public void Feedback(byte large, byte small) => FeedbackReceived?.Invoke(XboxFeedback.FromViGEm(large, small));
        public void Feedback(XboxFeedback feedback) => FeedbackReceived?.Invoke(feedback);
        public void Submit(ControllerInputState input) { events.Add("submit"); Inputs.Add(input); }
        public void Dispose() { Disposed = true; OnDispose?.Invoke(); events.Add("dispose xbox"); }
    }
    private sealed class FakeRumble : IXboxRumbleOutput
    {
        public List<(int Large, int Small)> Writes { get; } = [];
        public List<TriggerPair?> TriggerWrites { get; } = [];
        public bool Disposed, FailDispose;
        public void Write(byte large, byte small) => Writes.Add((large, small));
        public void Write(byte large, byte small, TriggerPair? triggers)
        {
            Write(large, small);
            TriggerWrites.Add(triggers);
        }
        public void Dispose() { Disposed = true; if (FailDispose) throw new IOException("Reset failed"); }
    }
}
