using System.Buffers.Binary;
using ForzaHaptics.Output;
using ForzaHaptics.Telemetry;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class TriggerTests
{
    [Fact]
    public void SupportedEffectsMatchIndependentGoldenBytes()
    {
        Assert.Equal(new byte[] { 0x21, 0xfe, 3, 0, 0, 0, 0, 0, 0, 0, 0 }, Bytes(TriggerEffect.Feedback(1)));
        Assert.Equal(new byte[] { 0x26, 0xff, 3, 0xff, 0xff, 0xff, 0x3f, 0, 0, 40, 0 },
            Bytes(TriggerEffect.Vibration(40, 8, 0)));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Feedback(0));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Vibration(30, 0));
        Assert.Equal(TriggerEffect.Off, TriggerEffect.Vibration(0, 8));
        Assert.Equal(8, TriggerEffect.Feedback(999).Strength);
        Assert.Contains("40 Hz", TriggerEffect.Vibration(40, 8).ToString());
    }

    [Theory]
    [InlineData(false, 1, 48)]
    [InlineData(true, 3, 78)]
    public void TransportOffsetsFlagsAndBluetoothCrcAreCorrect(bool bluetooth, int common, int size)
    {
        var pair = new TriggerPair(TriggerEffect.Feedback(2), TriggerEffect.Vibration(45, 1));
        byte[] report = TriggerHidWriter.BuildReport(bluetooth, pair, 15);
        Assert.Equal(size, report.Length);
        Assert.Equal(0x0c, report[common]);
        Assert.Equal(Bytes(pair.R2), report[(common + 10)..(common + 21)]);
        Assert.Equal(Bytes(pair.L2), report[(common + 21)..(common + 32)]);
        if (bluetooth)
        {
            Assert.Equal(new byte[] { 0x31, 0xf0, 0x10 }, report[..3]);
            uint crc = 0xffffffff;
            foreach (byte b in new byte[] { 0xa2 }.Concat(report[..^4]))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(report.Length - 4)));
            Assert.Equal(0, TriggerHidWriter.BuildReport(true, pair, 16)[1]);
        }
        else Assert.Equal(2, report[0]);
    }

    [Fact]
    public void SchedulerDeduplicatesLimitsChangesAndReleasesImmediately()
    {
        var scheduler = new TriggerWriteScheduler();
        var a = new TriggerPair(TriggerEffect.Feedback(1), TriggerEffect.Feedback(1));
        var b = new TriggerPair(TriggerEffect.Feedback(2), TriggerEffect.Feedback(2));
        Assert.True(scheduler.ShouldWrite(a, 0));
        scheduler.Written(a, 0);
        Assert.False(scheduler.ShouldWrite(a, 2));
        Assert.False(scheduler.ShouldWrite(b, .049));
        Assert.True(scheduler.ShouldWrite(b, .05));
        scheduler.Written(b, .05);
        Assert.True(scheduler.ShouldWrite(TriggerPair.Off, .051));
    }

    [Fact]
    public void EmergencyReleaseDoesNotBypassOtherChannelsNormalWriteInterval()
    {
        var scheduler = new TriggerWriteScheduler();
        var original = new TriggerPair(TriggerEffect.Feedback(1), TriggerEffect.Feedback(1));
        scheduler.Written(original, 0);
        var desired = new TriggerPair(TriggerEffect.Off, TriggerEffect.Feedback(8));
        var immediate = Assert.IsType<TriggerPair>(scheduler.SelectReport(desired, .01));
        Assert.Equal(TriggerEffect.Off, immediate.L2);
        Assert.Equal(original.R2, immediate.R2);
        scheduler.Written(immediate, .01);
        Assert.Null(scheduler.SelectReport(desired, .059));
        Assert.Equal(desired, scheduler.SelectReport(desired, .06));
    }

    private static byte[] Bytes(TriggerEffect effect)
    {
        var bytes = new byte[11];
        effect.WriteTo(bytes);
        return bytes;
    }
}
