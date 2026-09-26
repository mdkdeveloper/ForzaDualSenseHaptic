using ForzaHaptics.Controllers;
using ForzaHaptics.Haptics;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class TriggerRuntimeTests
{
    private static ControllerSnapshot Input(double now, byte right = 0) => new()
    {
        IsConnected = true, DeviceId = "selected", LastInputTick = 1000 + (long)(now * 1000),
        RightTrigger = right,
    };
    private static ForzaPacket Frame(double now) => new()
    {
        IsRaceOn = true, TimestampMs = (uint)(1000 + now * 1000), Gear = 3, Speed = 20, DrivetrainType = 2, TireSlipRatio = new(3, 3, 3, 3),
    };
    private static TriggerPair Warm(HapticBus bus, TelemetryProcessor telemetry, TriggerRuntime runtime)
    {
        for (int i = 0; i <= 50; i++)
        {
            double now = i / 100d;
            telemetry.Process(Frame(now), now);
            runtime.Step(now, 1000 + i * 10, Input(now, 255), "selected");
        }
        Assert.NotEqual(TriggerPair.Off, bus.Triggers);
        return bus.Triggers;
    }

    [Fact]
    public void ReleasedPhysicalPedalsStayOffEvenWhenForzaPedalsArePressed()
    {
        var bus = new HapticBus(); var config = new AppConfig();
        var telemetry = new TelemetryProcessor(bus, () => config);
        var runtime = new TriggerRuntime(bus, () => config);
        Warm(bus, telemetry, runtime);
        var frame = Frame(.51); frame.Accel = frame.Brake = 255;
        telemetry.Process(frame, .51);
        runtime.Step(.51, 1510, Input(.51), "selected");
        Assert.Equal(TriggerPair.Off, bus.Triggers);
    }
    [Theory]
    [InlineData("input")]
    [InlineData("telemetry")]
    [InlineData("device")]
    [InlineData("disconnect")]
    public void UnsafeOrStaleSourceImmediatelyDisablesBothChannels(string cause)
    {
        var bus = new HapticBus();
        var config = new AppConfig();
        var telemetry = new TelemetryProcessor(bus, () => config);
        var runtime = new TriggerRuntime(bus, () => config);
        Warm(bus, telemetry, runtime);
        double now = cause is "input" or "telemetry" ? .801 : .51;
        if (cause != "telemetry") telemetry.Process(Frame(now), now);
        var input = cause == "input" ? Input(.5) : Input(now);
        if (cause == "disconnect") input = input with { IsConnected = false };
        runtime.Step(now, 1000 + (long)(now * 1000), input, cause == "device" ? "different" : "selected");
        Assert.Equal(TriggerPair.Off, bus.Triggers);
    }

    [Fact]
    public void MenuAndProfileRevisionDropOldEventsAndRequireNewTelemetry()
    {
        var bus = new HapticBus();
        var config = new AppConfig();
        long revision = 1;
        var telemetry = new TelemetryProcessor(bus, () => config, () => revision);
        var runtime = new TriggerRuntime(bus, () => config, () => revision);
        Warm(bus, telemetry, runtime);
        revision++;
        runtime.Step(.51, 1510, Input(.51), "selected");
        Assert.Equal(TriggerPair.Off, bus.Triggers);
        telemetry.Process(Frame(.52), .52);
        runtime.Step(.52, 1520, Input(.52), "selected");
        var menu = Frame(.53);
        menu.IsRaceOn = false;
        telemetry.Process(menu, .53);
        runtime.Step(.53, 1530, Input(.53), "selected");
        Assert.Equal(TriggerPair.Off, bus.Triggers);
    }

    [Fact]
    public void AcceptedFramePublicationIsDetachedFromMutableCaller()
    {
        var bus = new HapticBus();
        var frame = Frame(0);
        bus.PublishTriggerTelemetry(frame, 0);
        frame.Gear = 6;
        Assert.True(bus.TriggerFrames.TryDequeue(out var published));
        Assert.Equal(3, published.Packet.Gear);
    }

    [Fact]
    public void TelemetryArrivingAfterTickClockIsDeferredWithoutLosingGearEvent()
    {
        var bus = new HapticBus(); var config = new AppConfig();
        config.Triggers.Throttle.SlipEnabled = false;
        config.Triggers.Throttle.LateralSlipEnabled = false;
        var telemetry = new TelemetryProcessor(bus, () => config);
        var runtime = new TriggerRuntime(bus, () => config);
        telemetry.Process(Frame(0), 0);
        runtime.Step(0, 1000, Input(0, 255), "selected");

        // The UDP producer sampled its clock just after the trigger loop sampled .01.
        var shifted = Frame(.0101); shifted.Gear = 4;
        telemetry.Process(shifted, .0101);
        runtime.Step(.01, 1010, Input(.01, 255), "selected");
        Assert.Single(bus.TriggerFrames);
        Assert.Contains("shifts seen 0", runtime.Status);

        runtime.Step(.02, 1020, Input(.02, 255), "selected");
        Assert.Empty(bus.TriggerFrames);
        Assert.Contains("shifts seen 1, started L2 0 R2 1", runtime.Status);
        runtime.Step(.08, 1080, Input(.08, 255), "selected");
        Assert.NotEqual(TriggerEffect.Off, bus.Triggers.R2);
    }

    [Fact]
    public void DeferredOrdinaryFramesPreserveGearBaselineAndActiveImpulse()
    {
        var bus = new HapticBus(); var config = new AppConfig();
        config.Triggers.Throttle.SlipEnabled = false;
        config.Triggers.Throttle.LateralSlipEnabled = false;
        var telemetry = new TelemetryProcessor(bus, () => config);
        var runtime = new TriggerRuntime(bus, () => config);
        telemetry.Process(Frame(0), 0);
        runtime.Step(0, 1000, Input(0, 255), "selected");
        telemetry.Process(Frame(.0101), .0101);
        runtime.Step(.01, 1010, Input(.01, 255), "selected");

        var shifted = Frame(.02); shifted.Gear = 4;
        telemetry.Process(shifted, .02);
        runtime.Step(.02, 1020, Input(.02, 255), "selected");
        runtime.Step(.08, 1080, Input(.08, 255), "selected");
        Assert.NotEqual(TriggerEffect.Off, bus.Triggers.R2);
        Assert.Contains("shifts seen 1, started L2 0 R2 1", runtime.Status);

        // A second race during the pulse must not create a sequence-gap reset.
        var ordinary = Frame(.0901); ordinary.Gear = 4;
        telemetry.Process(ordinary, .0901);
        runtime.Step(.09, 1090, Input(.09, 255), "selected");
        var next = Frame(.1); next.Gear = 4;
        telemetry.Process(next, .1);
        runtime.Step(.1, 1100, Input(.1, 255), "selected");
        Assert.Empty(bus.TriggerFrames);
        Assert.NotEqual(TriggerEffect.Off, bus.Triggers.R2);
        Assert.Contains("shifts seen 1, started L2 0 R2 1", runtime.Status);
    }
    [Fact]
    public void InvalidLateralSlipDoesNotReachEffects()
    {
        var frame = Frame(0);
        frame.TireSlipAngle.FL = float.NaN;
        Assert.False(ForzaPacket.TryParse(frame.ToBytes(), out _));
    }
}
