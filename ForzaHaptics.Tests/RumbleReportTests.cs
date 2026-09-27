using System.Buffers.Binary;
using ForzaHaptics.Controllers;
using ForzaHaptics.Emulation;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class RumbleReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedImpulseReportCarriesIndependentEffectsAndExplicitRelease(bool bluetooth)
    {
        var pair = new TriggerPair(TriggerEffect.Vibration(15, 2), TriggerEffect.Vibration(15, 7));
        var report = DualSenseRumbleOutput.BuildReport(bluetooth, 254, 128, 7, triggers: pair);
        int common = bluetooth ? 3 : 1;
        Assert.Equal(0x0f, report[common]);
        Assert.Equal(64, report[common + 2]);
        Assert.Equal(127, report[common + 3]);
        byte[] expected = new byte[11];
        pair.R2.WriteTo(expected);
        Assert.Equal(expected, report[(common + 10)..(common + 21)]);
        pair.L2.WriteTo(expected);
        Assert.Equal(expected, report[(common + 21)..(common + 32)]);
        if (bluetooth) AssertCrc(report);
        var stop = DualSenseRumbleOutput.BuildReport(bluetooth, 0, 0, triggers: TriggerPair.Off);
        Assert.Equal(0x0f, stop[common]);
        Assert.Equal(TriggerEffect.ModeOff, stop[common + 10]);
        Assert.Equal(TriggerEffect.ModeOff, stop[common + 21]);
        if (bluetooth) AssertCrc(stop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeRumbleOnlySelectsMotorsAndPreservesOtherEffects(bool bluetooth)
    {
        var report = DualSenseRumbleOutput.BuildReport(bluetooth, 254, 128, 5);
        int common = bluetooth ? 3 : 1;
        Assert.Equal(bluetooth ? 78 : 48, report.Length);
        Assert.Equal(bluetooth ? 0x31 : 0x02, report[0]);
        Assert.Equal(0x03, report[common]);
        Assert.Equal(64, report[common + 2]);
        Assert.Equal(127, report[common + 3]);
        // No valid bits or payload for triggers, audio volume, microphone, lightbar or player lights.
        Assert.Equal(0, report[common + 1]);
        Assert.All(report.Skip(common + 4).Take(bluetooth ? 74 - common - 4 : 48 - common - 4),
            value => Assert.Equal(0, value));
        if (bluetooth)
        {
            Assert.Equal(0x50, report[1]);
            Assert.Equal(0x10, report[2]);
            AssertCrc(report);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioResetClearsRumbleSelectionAndPayloadEvenForNonzeroArguments(bool bluetooth)
    {
        var report = DualSenseRumbleOutput.BuildReport(bluetooth, 255, 255, 255, restoreAudio: true);
        int common = bluetooth ? 3 : 1;
        Assert.All(report.Skip(common).Take((bluetooth ? 74 : 48) - common), value => Assert.Equal(0, value));
        if (bluetooth)
        {
            Assert.Equal(0xF0, report[1]);
            AssertCrc(report);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroMotorCommandStopsBothMotorsBeforeModeReset(bool bluetooth)
    {
        var report = DualSenseRumbleOutput.BuildReport(bluetooth, 0, 0);
        int common = bluetooth ? 3 : 1;
        Assert.Equal(3, report[common]);
        Assert.Equal(0, report[common + 2]);
        Assert.Equal(0, report[common + 3]);
        if (bluetooth) AssertCrc(report);
    }

    private static void AssertCrc(byte[] report) => Assert.Equal(
        DualSenseBatteryParser.ComputeCrc(0xA2, report.AsSpan(0, 74)),
        BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74)));
}
