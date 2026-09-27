using System.Buffers.Binary;
using ForzaHaptics.Controllers;
using ForzaHaptics.Emulation;

namespace ForzaHaptics.Tests;

public sealed class RumbleReportTests
{
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
