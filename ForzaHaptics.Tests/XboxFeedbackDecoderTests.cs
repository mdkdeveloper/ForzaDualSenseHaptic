using ForzaHaptics.Emulation;
using HIDMaestro;

namespace ForzaHaptics.Tests;

public sealed class XboxFeedbackDecoderTests
{
    [Theory]
    [InlineData(true, true, "ABC", null, true)]
    [InlineData(true, true, "old", null, false)]
    [InlineData(true, true, null, null, false)]
    [InlineData(false, true, "abc", null, false)]
    [InlineData(true, false, "abc", null, false)]
    [InlineData(true, true, "abc", "Access denied", false)]
    public void DriverPreflightRequiresBothPackagesAndMatchingSuccessfulInstallReceipt(
        bool main, bool companion, string? installed, string? error, bool expected)
    {
        var status = new HidMaestroXboxFactory.InstallationStatus(main, companion, "abc", installed, [], error);
        Assert.Equal(expected, status.MatchesBundle);
    }

    [Theory]
    [InlineData(short.MinValue, 0f)]
    [InlineData((short)0, .5f)]
    [InlineData(short.MaxValue, 1f)]
    public void InputAxesHaveExactNeutralAndEndpoints(short value, float expected)
        => Assert.Equal(expected, HidMaestroXboxFactory.Axis(value));

    [Theory]
    [InlineData(0x1000, HMButton.A)]
    [InlineData(0x2000, HMButton.B)]
    [InlineData(0x4000, HMButton.X)]
    [InlineData(0x8000, HMButton.Y)]
    [InlineData(0x0100, HMButton.LeftBumper)]
    [InlineData(0x0200, HMButton.RightBumper)]
    [InlineData(0x0010, HMButton.Start)]
    [InlineData(0x0020, HMButton.Back)]
    [InlineData(0x0040, HMButton.LeftStick)]
    [InlineData(0x0080, HMButton.RightStick)]
    [InlineData(0x0400, HMButton.Guide)]
    public void InputButtonsTranslateXboxBitLayout(ushort source, HMButton expected)
        => Assert.Equal(expected, HidMaestroXboxFactory.MapButtons(source));

    [Theory]
    [InlineData(0, HMHat.None)]
    [InlineData(1, HMHat.North)]
    [InlineData(9, HMHat.NorthEast)]
    [InlineData(8, HMHat.East)]
    [InlineData(10, HMHat.SouthEast)]
    [InlineData(2, HMHat.South)]
    [InlineData(6, HMHat.SouthWest)]
    [InlineData(4, HMHat.West)]
    [InlineData(5, HMHat.NorthWest)]
    [InlineData(3, HMHat.None)]
    public void InputHatIncludesDiagonalsAndCancelsOppositeDirections(ushort source, HMHat expected)
        => Assert.Equal(expected, HidMaestroXboxFactory.MapHat(source));

    private static XboxFeedback Decode(byte[] bytes, HMOutputSource source = HMOutputSource.HidOutput, byte report = 0)
        => XboxFeedbackDecoder.Decode(source, report, bytes, 42, 1234, DateTimeOffset.UnixEpoch);

    [Fact]
    public void CapturedFh6StopPacketUsesReportIdAsMotorMask()
    {
        // Unmodified 2026-09-27 drive capture: SDK ReportId=0x0F,
        // Data=00000000FF00EB. The descriptor has no report IDs.
        var feedback = Decode(Convert.FromHexString("00000000FF00EB"), report: 0x0F);
        Assert.True(feedback.IsValid);
        Assert.Equal(0f, feedback.LeftTrigger);
        Assert.Equal(0f, feedback.RightTrigger);
        Assert.True(feedback.LargePresent);
        Assert.True(feedback.SmallPresent);
        Assert.Equal(0, feedback.Large);
        Assert.Equal(0, feedback.Small);
        Assert.Equal(2550, feedback.DurationMs);
        Assert.Equal(0, feedback.DelayMs);
        Assert.Equal(235, feedback.RepeatCount);
        Assert.Equal(0x0F, feedback.ReportId);
        Assert.Equal("00000000FF00EB", Convert.ToHexString(feedback.Raw));
    }

    [Theory]
    [InlineData("00170506FF00EB", .23f, 13, 15)]
    [InlineData("0020644EFF00EB", .32f, 255, 199)]
    public void CapturedFh6DrivingPacketsDecodeActualTriggerAndBodyValues(
        string rawHex, float rightTrigger, byte large, byte small)
    {
        var feedback = Decode(Convert.FromHexString(rawHex), report: 0x0F);
        Assert.True(feedback.IsValid);
        Assert.Equal(0f, feedback.LeftTrigger);
        Assert.Equal(rightTrigger, feedback.RightTrigger);
        Assert.Equal(large, feedback.Large);
        Assert.Equal(small, feedback.Small);
        Assert.True(feedback.LargePresent);
        Assert.True(feedback.SmallPresent);
        Assert.Equal(2550, feedback.DurationMs);
        Assert.Equal(0, feedback.DelayMs);
        Assert.Equal(235, feedback.RepeatCount);
        Assert.Equal(rawHex, Convert.ToHexString(feedback.Raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(15)]
    public void BothKnownFramingsPreserveEveryMaskAndIndependentChannels(byte mask)
    {
        var split = Decode([25, 75, 100, 50, 30, 4, 2], report: mask);
        var prefixed = Decode([mask, 25, 75, 100, 50, 30, 4, 2]);
        Assert.True(split.IsValid);
        Assert.True(prefixed.IsValid);
        Assert.Equal((mask & 8) != 0 ? .25f : (float?)null, split.LeftTrigger);
        Assert.Equal((mask & 4) != 0 ? .75f : (float?)null, split.RightTrigger);
        Assert.Equal((mask & 2) != 0, split.LargePresent);
        Assert.Equal((mask & 1) != 0, split.SmallPresent);
        Assert.Equal(255, split.Large);
        Assert.Equal(128, split.Small);
        Assert.Equal(prefixed with { ReportId = split.ReportId, Raw = split.Raw }, split);
    }

    [Fact]
    public void FourChannelsAndTimingAreIndependentAndBufferIsOwned()
    {
        byte[] bytes = [15, 25, 75, 100, 0, 30, 4, 2];
        var feedback = Decode(bytes);
        bytes[1] = 100;
        Assert.True(feedback.IsValid);
        Assert.Equal(.25f, feedback.LeftTrigger);
        Assert.Equal(.75f, feedback.RightTrigger);
        Assert.Equal(255, feedback.Large);
        Assert.Equal(0, feedback.Small);
        Assert.Equal(300, feedback.DurationMs);
        Assert.Equal(40, feedback.DelayMs);
        Assert.Equal(2, feedback.RepeatCount);
        Assert.True(feedback.IsTimed);
        Assert.Equal(25, feedback.Raw[1]);
        Assert.Equal(42u, feedback.Sequence);
        Assert.Equal(1234, feedback.Timestamp);
        Assert.Equal(DateTimeOffset.UnixEpoch, feedback.ReceivedUtc);
    }

    [Fact]
    public void MaskDistinguishesMissingChannelFromExplicitZero()
    {
        var feedback = Decode([8, 0, 100, 100, 100, 1, 0, 0]);
        Assert.Equal(0f, feedback.LeftTrigger);
        Assert.Null(feedback.RightTrigger);
        Assert.False(feedback.LargePresent);
        Assert.False(feedback.SmallPresent);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void XInputNeverProducesTriggersEvenWithExtendedPayload(int length)
    {
        byte[] bytes = new byte[length];
        bytes[2] = bytes[3] = 99;
        bytes[4] = 2;
        var feedback = Decode(bytes, HMOutputSource.XInput);
        Assert.True(feedback.IsValid);
        Assert.Equal(99, feedback.Large);
        Assert.Equal(99, feedback.Small);
        Assert.Null(feedback.LeftTrigger);
        Assert.Null(feedback.RightTrigger);
        Assert.False(feedback.IsTimed);
    }

    [Fact]
    public void MalformedAndUnsupportedPacketsAreRetainedButNeverApplied()
    {
        XboxFeedback[] packets = [
            Decode([0, 0, 0, 0, 1, 0], report: 15),
            Decode([0, 0, 0, 0, 1, 0, 0], report: 0x10),
            Decode([101, 0, 0, 0, 1, 0, 0], report: 15),
            Decode([0, 101, 0, 0, 1, 0, 0], report: 15),
            Decode([0, 0, 101, 0, 1, 0, 0], report: 15),
            Decode([0, 0, 0, 101, 1, 0, 0], report: 15),
            Decode([15, 101, 0, 0, 0, 1, 0, 0]),
            Decode([255, 0, 0, 0, 0, 1, 0, 0]),
            Decode([15, 0, 0, 0, 0, 1, 0, 0], report: 3),
            Decode([15, 0, 0, 0, 0, 1, 0, 0], HMOutputSource.HidFeature),
            Decode([0, 0], HMOutputSource.XInput),
            Decode([0, 13, 0, 0, 1], HMOutputSource.XInput),
            Decode([0, 0, 40, 40, 255], HMOutputSource.XInput)
        ];
        Assert.All(packets, packet =>
        {
            Assert.False(packet.IsValid);
            Assert.NotEmpty(packet.RejectionReason!);
            Assert.NotEmpty(packet.Raw);
            Assert.Null(packet.LeftTrigger);
            Assert.Null(packet.RightTrigger);
        });
    }

    [Fact]
    public void ViGEmDoesNotClaimImpulseSupport()
    {
        var feedback = XboxFeedback.FromViGEm(10, 20);
        Assert.Equal(10, feedback.Large);
        Assert.Equal(20, feedback.Small);
        Assert.Null(feedback.LeftTrigger);
        Assert.Null(feedback.RightTrigger);
    }
}
