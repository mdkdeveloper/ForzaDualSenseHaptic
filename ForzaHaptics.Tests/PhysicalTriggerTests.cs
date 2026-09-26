using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class PhysicalTriggerTests
{
    [Fact]
    public void TenZonesUseIndependentMaskAndThreeBitLevels()
    {
        var effect = TriggerEffect.Feedback(new float[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 0 });
        var bytes = new byte[11]; effect.WriteTo(bytes);
        Assert.Equal(new byte[] { 0x21, 0xfe, 1, 0x40, 0x34, 0xd6, 7, 0, 0, 0, 0 }, bytes);
        for (int i = 0; i < 10; i++) Assert.Equal(i is > 0 and < 9 ? i : 0, effect.GetZoneStrength(i));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Feedback(new float[10]));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Feedback(new float[] { 1, 1, 1, 1, float.NaN, 1, 1, 1, 1, 1 }));
    }

    [Fact]
    public void NoPassiveResistanceRegardlessOfPhysicalOrForzaPedals()
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        for (int i = 0; i <= 20; i++)
        {
            p.Accel = (byte)(i * 12); p.Brake = (byte)(255 - i * 12);
            p.Acceleration.X = p.Acceleration.Z = i; p.Boost = i; p.HandBrake = 255;
            processor.AcceptTelemetry(p, c, i * .01);
            Assert.Equal(TriggerPair.Off, processor.Tick(c, (byte)(i * 12), (byte)(255 - i * 12), i * .01));
        }
    }
    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(144)] [InlineData(240)]
    public void ShiftStartsWithoutCurveDelayAndLastsThreeHundredMillisecondsAcrossForzaRates(int fps)
    {
        var c = new TriggersConfig(); var p = Packet();
        var processor = new TriggerProcessor(); var values = new List<(double Time, TriggerPair Pair, string Status)>();
        double nextPacket = 0;
        for (int i = 0; i <= 100; i++)
        {
            double now = i / 100.0;
            while (nextPacket <= now + 1e-9)
            {
                p = Packet(); p.Gear = (byte)(nextPacket >= .3 - 1e-9 ? 3 : 2);
                processor.AcceptTelemetry(p, c, nextPacket); nextPacket += 1.0 / fps;
            }
            values.Add((now, processor.Tick(c, 255, 255, now), processor.Status));
        }
        var active = values.Where(x => x.Pair.R2.Mode == TriggerEffect.ModeVibration).ToList();
        Assert.NotEmpty(active);
        Assert.InRange(active[0].Time, .30, .38);
        Assert.InRange(active[^1].Time, .57, .66);
        Assert.Contains(active, x => x.Pair.R2.Strength >= 4);
        Assert.DoesNotContain(values, x => x.Pair.L2.Mode == TriggerEffect.ModeVibration);
        Assert.Equal(TriggerEffect.ModeOff, values[^1].Pair.R2.Mode);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void ShiftDirectionMapsToPhysicalPedals(bool down, bool rightExpected, bool leftExpected)
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 255, 255);
        p.Gear = (byte)(down ? 1 : 3);
        var values = Run(processor, c, p, .2, .7, 255, 255);
        Assert.Equal(rightExpected, values.Any(x => x.R2.Mode == TriggerEffect.ModeVibration));
        Assert.Equal(leftExpected, values.Any(x => x.L2.Mode == TriggerEffect.ModeVibration));
    }

    [Fact]
    public void EventsAreIndependentOfLegacyMode()
    {
        var c = new TriggersConfig(); c.Throttle.Mode = TriggerMode.Resistance;
        var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 0, 255);
        p.Gear = 3;
        Assert.Contains(Run(processor, c, p, .2, .6, 0, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
        c.Throttle.Enabled = false;
        Assert.Equal(TriggerEffect.Off, processor.Tick(c, 0, 255, .61).R2);
    }

    [Fact]
    public void NewChangesDoNotExtendActiveShiftOrQueueAnotherOne()
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 0, 255);
        p.Gear = 3; Run(processor, c, p, .2, .35, 0, 255);
        p.Gear = 4;
        var values = Run(processor, c, p, .35, 1, 0, 255);
        Assert.DoesNotContain(values.Skip(20), x => x.R2.Mode == TriggerEffect.ModeVibration);
    }

    [Theory]
    [InlineData(0)] [InlineData(11)]
    public void NeutralAndReverseDoNotGenerateShift(int gear)
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 255, 255); p.Gear = (byte)gear;
        Assert.DoesNotContain(Run(processor, c, p, .2, .8, 255, 255),
            x => x.R2.Mode == TriggerEffect.ModeVibration || x.L2.Mode == TriggerEffect.ModeVibration);
        p.Gear = 1;
        Assert.DoesNotContain(Run(processor, c, p, .8, 1.2, 255, 255),
            x => x.R2.Mode == TriggerEffect.ModeVibration || x.L2.Mode == TriggerEffect.ModeVibration);
    }

    [Theory]
    [InlineData(false, .1)] [InlineData(true, .1)]
    [InlineData(false, .5)] [InlineData(true, .5)]
    public void TransientNeutralPreservesOneShiftWithoutSlip(bool downshift, double neutralDuration)
    {
        var c = new TriggersConfig();
        c.Throttle.SlipEnabled = c.Throttle.LateralSlipEnabled = c.Brake.SlipEnabled = false;
        var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 255, 255);
        p.Gear = 11;
        var neutral = Run(processor, c, p, .2, .2 + neutralDuration, 255, 255);
        Assert.All(neutral, pair => Assert.Equal(TriggerPair.Off, pair));
        p.Gear = (byte)(downshift ? 1 : 3);
        var values = Run(processor, c, p, .2 + neutralDuration, .8 + neutralDuration, 255, 255);
        Assert.Contains(values, pair => pair.R2.Mode == TriggerEffect.ModeVibration);
        Assert.Equal(downshift, values.Any(pair => pair.L2.Mode == TriggerEffect.ModeVibration));
        Assert.Contains($"shifts seen 1, started L2 {(downshift ? 1 : 0)} R2 1", processor.Status);
        Assert.Equal(TriggerPair.Off, values[^1]);
    }

    [Theory]
    [InlineData(11, .501, 3)]
    [InlineData(11, .1, 2)]
    [InlineData(0, .1, 3)]
    [InlineData(12, .1, 3)]
    public void ProlongedNeutralSameGearReverseAndInvalidDoNotCreateShift(int intermediate, double duration, int finalGear)
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        c.Throttle.SlipEnabled = c.Throttle.LateralSlipEnabled = c.Brake.SlipEnabled = false;
        Run(processor, c, p, 0, .2, 255, 255);
        p.Gear = (byte)intermediate;
        Run(processor, c, p, .2, .2 + duration, 255, 255);
        p.Gear = (byte)finalGear;
        Assert.All(Run(processor, c, p, .2 + duration, .8 + duration, 255, 255),
            pair => Assert.Equal(TriggerPair.Off, pair));
        Assert.Contains("shifts seen 0", processor.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ResetOrMenuClearsPendingNeutralBridge(bool menu)
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 255, 255);
        p.Gear = 11; Run(processor, c, p, .2, .25, 255, 255);
        if (menu)
        {
            p.IsRaceOn = false;
            processor.AcceptTelemetry(p, c, .25);
            p.IsRaceOn = true;
        }
        else processor.Reset();
        p.Gear = 3;
        Assert.All(Run(processor, c, p, .26, .8, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));
        Assert.Contains("shifts seen 0", processor.Status);
    }
    [Fact]
    public void ExpiredOrReleasedShiftIsNotReplayed()
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 0, 0);
        p.Gear = 3; processor.AcceptTelemetry(p, c, .2);
        Assert.Equal(TriggerEffect.ModeOff, processor.Tick(c, 0, 255, .36).R2.Mode);
        Run(processor, c, p, .37, .6, 0, 255);
        p.Gear = 4; Run(processor, c, p, .6, .72, 0, 255);
        Assert.Equal(TriggerEffect.ModeOff, processor.Tick(c, 0, 0, .72).R2.Mode);
        Assert.DoesNotContain(Run(processor, c, p, .73, 1, 0, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
    }

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(2, true)] [InlineData(99, false)]
    public void LongitudinalSlipUsesDrivenWheels(int drivetrain, bool expected)
    {
        var c = new TriggersConfig(); var p = Packet(); p.DrivetrainType = drivetrain;
        p.TireSlipRatio = new Wheels(-3, -3, 0, 0);
        var values = Run(new TriggerProcessor(), c, p, 0, .5, 0, 255);
        Assert.Equal(expected, values.Any(x => x.R2.Mode == TriggerEffect.ModeVibration));
    }

    [Fact]
    public void PhysicalBrakeSuppressesThrottleSlipButNotLateralSlip()
    {
        var c = new TriggersConfig(); var p = Packet(); p.TireSlipRatio = new Wheels(3, 3, 3, 3);
        var processor = new TriggerProcessor();
        Assert.DoesNotContain(Run(processor, c, p, 0, .5, 255, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.TireSlipAngle = new Wheels(0, 0, -3, -3);
        Assert.Contains(Run(processor, c, p, .5, 1, 255, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.Brake = 255;
        Assert.Equal(TriggerEffect.ModeVibration, Run(new TriggerProcessor(), c, p, 0, .5, 0, 255)[^1].R2.Mode);
    }

    [Fact]
    public void CombinedSlipDoesNotBecomeLateralSignalAndTwoSourcesDoNotAdd()
    {
        var c = new TriggersConfig(); var p = Packet(); p.TireCombinedSlip = new Wheels(5, 5, 5, 5);
        Assert.DoesNotContain(Run(new TriggerProcessor(), c, p, 0, .5, 0, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.TireSlipRatio = new Wheels(3, 3, 3, 3);
        var one = Run(new TriggerProcessor(), c, p, 0, .5, 0, 255)[^1].R2;
        p.TireSlipAngle = new Wheels(3, 3, 3, 3);
        Assert.Equal(one, Run(new TriggerProcessor(), c, p, 0, .5, 0, 255)[^1].R2);
    }

    [Fact]
    public void RepeatedSlipHasSilentGapsWithoutApplyingResistance()
    {
        var c = new TriggersConfig(); c.Throttle.SlipMode = TriggerSlipMode.Repeated;
        var p = Packet(); p.TireSlipRatio = new Wheels(3, 3, 3, 3);
        var values = Run(new TriggerProcessor(), c, p, 0, 2, 0, 255);
        Assert.Contains(values.Skip(60), x => x.R2.Mode == TriggerEffect.ModeVibration);
        Assert.Contains(values.Skip(60), x => x.R2 == TriggerEffect.Off);
        Assert.DoesNotContain(values, x => x.R2.Mode == TriggerEffect.ModeFeedback);
        c.Throttle.SlipMode = TriggerSlipMode.Continuous;
        Assert.All(Run(new TriggerProcessor(), c, p, 0, 2, 0, 255).Skip(20),
            x => Assert.Equal(TriggerEffect.ModeVibration, x.R2.Mode));
    }

    [Fact]
    public void ShiftPreemptsSlipAndSlipResumesAfterItsDeadline()
    {
        var c = new TriggersConfig(); var p = Packet(); p.TireSlipRatio = new Wheels(3, 3, 3, 3);
        var processor = new TriggerProcessor(); Run(processor, c, p, 0, .4, 0, 255);
        p.Gear = 3; var shift = Run(processor, c, p, .4, .8, 0, 255);
        Assert.Contains(shift, x => x.R2.Mode == TriggerEffect.ModeVibration && x.R2.ParamsHi == 20);
        Assert.Equal(45, Run(processor, c, p, .8, 1.1, 0, 255)[^1].R2.ParamsHi);
    }

    [Fact]
    public void CollisionNeedsAnEdgeHasFixedDeadlineAndCannotOverrideShift()
    {
        var c = new TriggersConfig(); c.Collision.Enabled = true; c.Collision.Amp = 1;
        var p = Packet(); p.SmashableVelDiff = 10; var processor = new TriggerProcessor();
        Assert.DoesNotContain(Run(processor, c, p, 0, .4, 0, 255), x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.SmashableVelDiff = 0; Run(processor, c, p, .4, .5, 0, 255);
        p.SmashableVelDiff = 10;
        var collision = Run(processor, c, p, .5, 1, 0, 255);
        Assert.Contains(collision, x => x.R2.Mode == TriggerEffect.ModeVibration);
        Assert.DoesNotContain(collision.Skip(15), x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.SmashableVelDiff = 0; Run(processor, c, p, 1, 1.1, 0, 255);
        p.SmashableVelDiff = 10; p.Gear = 3;
        Assert.Contains(Run(processor, c, p, 1.1, 1.4, 0, 255), x => x.R2.ParamsHi == 20);
    }

    [Fact]
    public void PauseResetInvalidValuesAndZeroStrengthRelease()
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .1, 0, 0); c.Strength = 0;
        Assert.Equal(TriggerPair.Off, processor.Tick(c, 255, 255, .1));
        c.Strength = .65f; p.IsRaceOn = false; processor.AcceptTelemetry(p, c, .11);
        Assert.Equal(TriggerPair.Off, processor.Tick(c, 255, 255, .11));
        p.IsRaceOn = true; p.TireSlipAngle.FL = float.NaN; processor.AcceptTelemetry(p, c, .12);
        Assert.Equal(TriggerPair.Off, processor.Tick(c, 255, 255, .12));
        processor.Reset(); Assert.Equal(TriggerPair.Off, processor.Tick(c, 255, 255, .13));
    }

    [Fact]
    public void NewShiftAtFirstTickAfterPreviousDeadlineStartsANewPulse()
    {
        var c = new TriggersConfig(); var p = Packet(); var processor = new TriggerProcessor();
        Run(processor, c, p, 0, .2, 0, 255);
        p.Gear = 3;
        var first = Run(processor, c, p, .2, .5, 0, 255);
        Assert.Contains(first, x => x.R2.Mode == TriggerEffect.ModeVibration);
        p.Gear = 4;
        // The previous pulse entered immediately at .2 and expired at .5.
        var second = Run(processor, c, p, .5, .9, 0, 255);
        Assert.Contains(second.Take(20), x => x.R2.Mode == TriggerEffect.ModeVibration);
        Assert.DoesNotContain(second.Skip(30), x => x.R2.Mode == TriggerEffect.ModeVibration);
    }
    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(144)] [InlineData(240)]
    public void ShiftSurvivesTwentyHzSchedulerWithDistinctPlateauAndImmediateRelease(int fps)
    {
        var config = new TriggersConfig(); var processor = new TriggerProcessor();
        var scheduler = new TriggerWriteScheduler();
        var reports = new List<(double Time, TriggerPair Pair)>();
        double nextPacket = 0;
        for (int i = 0; i <= 100; i++)
        {
            double now = i / 100d;
            while (nextPacket <= now + 1e-9)
            {
                var packet = Packet(); packet.Gear = (byte)(nextPacket >= .3 - 1e-9 ? 3 : 2);
                processor.AcceptTelemetry(packet, config, nextPacket);
                nextPacket += 1d / fps;
            }
            var selected = scheduler.SelectReport(processor.Tick(config, 0, 120, now), now);
            if (selected == null) continue;
            scheduler.Written(selected, now); reports.Add((now, selected));
            var report = TriggerHidWriter.BuildReport(true, selected);
            Assert.Equal(selected.R2.Mode, report[13]);
            Assert.Equal((byte)selected.R2.ParamsHi, report[22]);
        }
        var peak = reports.First(x => x.Pair.R2.Strength == 4);
        var lower = reports.First(x => x.Time > peak.Time && x.Pair.R2.Strength < 4);
        Assert.True(lower.Time - peak.Time >= .15 - 1e-9);
        Assert.Equal(20, peak.Pair.R2.ParamsHi);
        Assert.DoesNotContain(reports, x => x.Pair.R2.Mode == TriggerEffect.ModeFeedback);
        Assert.Equal(TriggerPair.Off, reports[^1].Pair);
        Assert.Contains("shifts seen 1, started L2 0 R2 1", processor.Status);

        var frame = Packet(); frame.Gear = 4; processor.AcceptTelemetry(frame, config, 1.01);
        for (int i = 101; i <= 110; i++) processor.Tick(config, 0, 120, i / 100d);
        Assert.Equal(TriggerEffect.ModeVibration, processor.Tick(config, 0, 120, 1.11).R2.Mode);
        scheduler.Written(new(TriggerEffect.Off, TriggerEffect.Vibration(20, 4)), 1.11);
        var released = processor.Tick(config, 0, 10, 1.12);
        Assert.Equal(TriggerPair.Off, released);
        Assert.Equal(released, scheduler.SelectReport(released, 1.12));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearEventsRemainIndependentWhenEverySlipCueIsDisabled(bool downshift)
    {
        var config = new TriggersConfig();
        config.Throttle.SlipEnabled = false;
        config.Throttle.LateralSlipEnabled = false;
        config.Brake.SlipEnabled = false;
        var packet = Packet();
        packet.TireSlipRatio = new Wheels(3, 3, 3, 3);
        packet.TireSlipAngle = new Wheels(3, 3, 3, 3);
        var processor = new TriggerProcessor();
        Assert.All(Run(processor, config, packet, 0, .3, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));

        packet.Gear = (byte)(downshift ? 1 : 3);
        var shift = Run(processor, config, packet, .3, .8, 255, 255);
        Assert.Contains(shift, pair => pair.R2.Mode == TriggerEffect.ModeVibration && pair.R2.Strength >= 4);
        Assert.Equal(downshift, shift.Any(pair => pair.L2.Mode == TriggerEffect.ModeVibration));
        foreach (var effect in shift.SelectMany(pair => new[] { pair.L2, pair.R2 }).Where(effect => effect.Mode != TriggerEffect.ModeOff))
            Assert.Equal((ushort)config.GearShift.Freq, effect.ParamsHi);
        Assert.All(Run(processor, config, packet, .8, 1.5, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));
    }

    [Fact]
    public void DisabledChannelsSuppressGearsAsWellAsSlip()
    {
        var config = new TriggersConfig();
        config.Throttle.Enabled = config.Brake.Enabled = false;
        var packet = Packet();
        packet.TireSlipRatio = packet.TireSlipAngle = new Wheels(3, 3, 3, 3);
        var processor = new TriggerProcessor();
        Assert.All(Run(processor, config, packet, 0, .3, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));
        packet.Gear = 3;
        Assert.All(Run(processor, config, packet, .3, .8, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));
        packet.Gear = 2;
        Assert.All(Run(processor, config, packet, .8, 1.3, 255, 255), pair => Assert.Equal(TriggerPair.Off, pair));
    }

    [Fact]
    public void ManualTriggerTestContainsOnlyAlternatingGearEvents()
    {
        for (int i = 0; i < 1200; i++)
        {
            double age = i / 100.0;
            var packet = HapticEngine.CreateTriggerTestPacket(age);
            Assert.Equal((byte)(((int)(age / 2) % 2 == 0) ? 2 : 3), packet.Gear);
            Assert.Equal(0f, packet.SmashableVelDiff);
            for (int wheel = 0; wheel < 4; wheel++)
            {
                Assert.Equal(0f, packet.TireSlipRatio[wheel]);
                Assert.Equal(0f, packet.TireSlipAngle[wheel]);
                Assert.Equal(0f, packet.SurfaceRumble[wheel]);
            }
        }
    }
    private static ForzaPacket Packet() => new() { IsRaceOn = true, Speed = 20, Gear = 2, DrivetrainType = 2 };
    private static List<TriggerPair> Run(TriggerProcessor processor, TriggersConfig config, ForzaPacket packet,
        double start, double end, byte left, byte right)
    {
        var values = new List<TriggerPair>();
        for (int i = 0; start + i * .01 < end - 1e-9; i++)
        {
            double now = start + i * .01;
            processor.AcceptTelemetry(packet, config, now);
            values.Add(processor.Tick(config, left, right, now));
        }
        return values;
    }
}
