using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ForzaHaptics.Haptics;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Util;

namespace ForzaHaptics.Tests;

public sealed class TelemetrySafetyTests
{
    private static ForzaPacket Frame(uint timestamp = 1000) => new()
    {
        IsRaceOn = true, TimestampMs = timestamp, CarOrdinal = 1,
        EngineMaxRpm = 7000, EngineIdleRpm = 800, CurrentEngineRpm = 3000,
        Speed = 20, Accel = 128, Gear = 2,
        NormalizedSuspensionTravel = new Wheels(0.5f, 0.5f, 0.5f, 0.5f),
    };

    private static AppConfig Config()
    {
        var config = new AppConfig();
        config.Triggers.Enabled = false;
        return config;
    }

    [Theory]
    [InlineData(323, true)]
    [InlineData(324, true)]
    [InlineData(322, false)]
    [InlineData(325, false)]
    [InlineData(331, false)]
    [InlineData(400, false)]
    public void PacketAcceptsOnlyExplicitSupportedLengths(int length, bool expected)
    {
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), 2500);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(256, 4), 20);
        bytes[315] = 127;
        bytes[319] = 4;
        Assert.Equal(expected, ForzaPacket.TryParse(bytes, out var packet));
        if (expected)
        {
            Assert.Equal(72, packet.SpeedKmh);
            Assert.Equal(2500, packet.CurrentEngineRpm);
            Assert.Equal(4, packet.Gear);
            Assert.Equal(127 / 255f, packet.Throttle01);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(40)]
    [InlineData(68)]
    [InlineData(84)]
    [InlineData(148)]
    [InlineData(180)]
    [InlineData(196)]
    [InlineData(236)]
    [InlineData(256)]
    [InlineData(284)]
    public void NonfiniteConsumedFloatsAreRejected(int offset)
    {
        var bytes = Frame().ToBytes();
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), value);
            Assert.False(ForzaPacket.TryParse(bytes, out _));
        }
    }

    [Fact]
    public void DuplicateBackwardAndInvalidInputCannotKeepFeedbackAlive()
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        processor.Process(Frame(), 0);
        Assert.True(bus.Targets.EngineAmp > 0);
        processor.Process(Frame(), 0.15);
        processor.Process(Frame(999), 0.2);
        var invalid = Frame(1001);
        invalid.SurfaceRumble.FL = float.NaN;
        processor.Process(invalid, 0.31);
        Assert.Equal(1, processor.Counters.Packets);
        Assert.Equal(0, bus.Status.LastPacketTime);
        Assert.Same(HapticTargets.Silent, bus.Targets);
        processor.Process(Frame(1016), 0.32);
        Assert.True(bus.Targets.EngineAmp > 0);
    }

    [Fact]
    public void TimestampWrapIsForwardAndCarChangeResetsWithoutFalseEvents()
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        processor.Process(Frame(uint.MaxValue - 10), 0);
        long generation = bus.ResetGeneration;
        processor.Process(Frame(5), 0.016);
        Assert.Equal(generation, bus.ResetGeneration);
        Assert.Equal(2, processor.Counters.Packets);
        var otherCar = Frame(20);
        otherCar.CarOrdinal = 2;
        otherCar.Gear = 5;
        otherCar.WheelInPuddle = new Wheels(1, 1, 1, 1);
        otherCar.SmashableVelDiff = 12;
        otherCar.Acceleration = new Vector3(100, 0, 100);
        otherCar.SuspensionTravelMeters = new Wheels(1, 1, 1, 1);
        processor.Process(otherCar, 0.032);
        Assert.True(bus.ResetGeneration > generation);
        Assert.Empty(bus.Kicks);
        Assert.Equal(0, processor.Counters.Shifts + processor.Counters.Splashes + processor.Counters.Smashes + processor.Counters.Impacts);
    }

    [Theory]
    [InlineData(1400u, 0.016)]
    [InlineData(600u, 0.016)]
    [InlineData(1016u, 0.26)]
    public void TimestampOrArrivalDiscontinuityStartsAQuietEventBaseline(uint timestamp, double now)
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        processor.Process(Frame(), 0);
        long generation = bus.ResetGeneration;
        var changed = Frame(timestamp);
        changed.Gear = 5;
        changed.SmashableVelDiff = 20;
        changed.Acceleration = new Vector3(120, 0, 0);
        changed.WheelInPuddle = new Wheels(1, 1, 1, 1);
        changed.SuspensionTravelMeters = new Wheels(1, 1, 1, 1);
        processor.Process(changed, now);
        Assert.True(bus.ResetGeneration > generation);
        Assert.Empty(bus.Kicks);
        Assert.Equal(2, processor.Counters.Packets);
    }

    [Fact]
    public void RaceOffWithDuplicateTimestampStillSilencesImmediately()
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        processor.Process(Frame(), 0);
        var paused = Frame();
        paused.IsRaceOn = false;
        processor.Process(paused, 0.01);
        Assert.False(bus.Status.RaceOn);
        Assert.Same(HapticTargets.Silent, bus.Targets);
    }

    [Fact]
    public void RevisionAndTimeoutClearEventHistoryAndOldKicks()
    {
        var bus = new HapticBus();
        var config = Config();
        long revision = 0;
        var processor = new TelemetryProcessor(bus, () => config, () => revision);
        processor.Process(Frame(), 0);
        bus.Kick(new HapticKick(1, 1, 80, 0.1f, KickKind.Sine));
        long generation = bus.ResetGeneration;
        revision++;
        processor.CheckTimeout(0.1);
        Assert.Empty(bus.Kicks);
        Assert.True(bus.ResetGeneration > generation);
        var resumed = Frame(1016);
        resumed.Gear = 5;
        processor.Process(resumed, 0.12);
        Assert.Equal(0, processor.Counters.Shifts);
        processor.CheckTimeout(0.43);
        Assert.Same(HapticTargets.Silent, bus.Targets);
        resumed = Frame(1032);
        resumed.Gear = 6;
        processor.Process(resumed, 0.44);
        Assert.Equal(0, processor.Counters.Shifts);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void StrongBumpsHaveHardTimeLimitAtEveryPacketRate(int rate)
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        for (int i = 0; i <= rate; i++)
        {
            var packet = Frame(1000 + (uint)Math.Round(i * 1000d / rate));
            packet.SuspensionTravelMeters = new Wheels(i * 4f / rate, 0, 0, 0);
            processor.Process(packet, i / (double)rate);
        }
        Assert.InRange(processor.Counters.Bumps, 14, 17);
        Assert.All(bus.Kicks, kick => Assert.True(kick.AmpL > kick.AmpR));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void SustainedCollisionAndSmashDoNotRepeat(int rate)
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        for (int i = 0; i <= rate; i++)
        {
            var packet = Frame(1000 + (uint)Math.Round(i * 1000d / rate));
            if (i > 0)
            {
                packet.Acceleration = new Vector3(120, 0, 0);
                packet.SmashableVelDiff = 12;
            }
            processor.Process(packet, i / (double)rate);
        }
        Assert.Equal(1, processor.Counters.Impacts);
        Assert.Equal(1, processor.Counters.Smashes);
    }

    [Fact]
    public void WaterStopsAtRestAndContactHeuristicHasHysteresis()
    {
        var bus = new HapticBus();
        var config = Config();
        var processor = new TelemetryProcessor(bus, () => config);
        var packet = Frame();
        packet.Speed = 0;
        packet.WheelInPuddle = new Wheels(1, 1, 1, 1);
        processor.Process(packet, 0);
        Assert.All(bus.Targets.Water, value => Assert.Equal(0, value));
        Assert.Equal(0, processor.Counters.Splashes);
        packet = Frame(1016);
        packet.NormalizedSuspensionTravel = new Wheels(0.009f, 0.009f, 0.009f, 0.009f);
        processor.Process(packet, 0.016);
        Assert.All(bus.Targets.Road, value => Assert.True(value > 0));
    }

    [Fact]
    public async Task ContinuousMalformedUdpTrafficStillExpiresValidFeedback()
    {
        var bus = new HapticBus();
        var config = new AppConfig();
        var processor = new TelemetryProcessor(bus, () => config);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggers = new ForzaHaptics.Triggers.TriggerRuntime(bus, () => config);
        var expired = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new UdpTelemetryReceiver(0, Array.Empty<string>(), null,
            packet => { processor.Process(packet, Clock.Now); received.TrySetResult(); },
            () =>
            {
                processor.CheckTimeout(Clock.Now);
                triggers.Step(Clock.Now, Environment.TickCount64, new ForzaHaptics.Controllers.ControllerSnapshot
                {
                    IsConnected = true, DeviceId = "test", LastInputTick = Environment.TickCount64,
                    LeftTrigger = 0, RightTrigger = 255,
                }, "test");
                if (ReferenceEquals(bus.Targets, HapticTargets.Silent) && double.IsFinite(bus.Status.LastPacketTime))
                    expired.TrySetResult(Clock.Now - bus.Status.LastPacketTime);
            });
        using var sender = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.Port);
        for (uint i = 0; i < 12; i++)
        {
            var frame = Frame(1000 + 20 * i);
            frame.Accel = 255;
            frame.TireSlipRatio = new Wheels(3, 3, 3, 3);
            await sender.SendAsync(frame.ToBytes(), endpoint, TestContext.Current.CancellationToken);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(bus.Targets.EngineAmp > 0);
        Assert.NotEqual(ForzaHaptics.Triggers.TriggerEffect.Off, bus.Triggers.R2);
        var nonfinite = Frame(1300).ToBytes();
        BinaryPrimitives.WriteSingleLittleEndian(nonfinite.AsSpan(148, 4), float.NaN);
        for (int i = 0; i < 25; i++)
        {
            await sender.SendAsync(i % 2 == 0 ? new byte[12] : nonfinite, endpoint, TestContext.Current.CancellationToken);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.True(receiver.Invalid > 0);
        Assert.Same(HapticTargets.Silent, bus.Targets);
        Assert.Same(ForzaHaptics.Triggers.TriggerPair.Off, bus.Triggers);
        double silenceDelay = await expired.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.InRange(silenceDelay, TelemetryProcessor.TimeoutSeconds, TelemetryProcessor.TimeoutSeconds + 0.25);
    }
}
