using ForzaHaptics.Emulation;
using ForzaHaptics.Triggers;

namespace ForzaHaptics.Tests;

public sealed class ImpulsePlaybackTests
{
    private static XboxFeedback Impulse(float left, float right, long now = 1000) =>
        XboxFeedback.FromViGEm(100, 50) with { LeftTrigger = left, RightTrigger = right, Timestamp = now };

    [Fact]
    public void ChannelsStayIndependentAndBodyOnlyUpdatesDoNotEraseTriggers()
    {
        var player = new ImpulsePlayback();
        player.Accept(Impulse(.25f, 1f), 1000);
        var first = Assert.IsType<EmulationOutput>(player.Select(1000));
        Assert.Equal(2, first.Triggers!.L2.Strength);
        Assert.Equal(8, first.Triggers.R2.Strength);
        Assert.Equal(15, first.Triggers.L2.ParamsHi);
        Assert.Equal(0, first.Triggers.L2.GetZoneStrength(0));
        player.Written(first, 1000);
        player.Accept(XboxFeedback.FromViGEm(40, 60), 1001);
        var body = Assert.IsType<EmulationOutput>(player.Select(1001));
        Assert.Equal(40, body.Large);
        Assert.Equal(first.Triggers, body.Triggers);
    }

    [Fact]
    public void ReleaseBypassesRateLimitWithoutIncreasingOtherTrigger()
    {
        var player = new ImpulsePlayback();
        player.Accept(Impulse(.5f, .25f), 1000);
        player.Written(player.Select(1000)!, 1000);
        player.Accept(Impulse(0, 1), 1001);
        var released = Assert.IsType<EmulationOutput>(player.Select(1001));
        Assert.Equal(TriggerEffect.Off, released.Triggers!.L2);
        Assert.Equal(2, released.Triggers.R2.Strength);
        player.Written(released, 1001);
        Assert.Null(player.Select(1020));
        Assert.Equal(8, player.Select(1021)!.Triggers!.R2.Strength);
    }

    [Fact]
    public void DelayDurationAndRepeatAdvanceWithoutNewCallbacksAndExpire()
    {
        var player = new ImpulsePlayback();
        player.Accept(Impulse(1, .5f) with { IsTimed = true, DelayMs = 20, DurationMs = 40, RepeatCount = 1 }, 1000);
        Assert.Equal(TriggerPair.Off, player.Select(1000)!.Triggers);
        Assert.Equal(0, player.Select(1019)!.Large);
        Assert.Equal(8, player.Select(1020)!.Triggers!.L2.Strength);
        Assert.Equal(100, player.Select(1059)!.Large);
        Assert.Equal(TriggerPair.Off, player.Select(1060)!.Triggers);
        Assert.Equal(8, player.Select(1080)!.Triggers!.L2.Strength);
        Assert.Equal(TriggerPair.Off, player.Select(1120)!.Triggers);
        Assert.Equal(0, player.Select(1120)!.Large);
    }

    [Fact]
    public void MaskedUpdatePreservesOtherChannelsAndNewCommandReplacesSchedule()
    {
        var player = new ImpulsePlayback();
        player.Accept(Impulse(1, .5f), 1000);
        player.Accept(Impulse(0, 0) with { LeftTrigger = null, LargePresent = false, SmallPresent = false }, 1010);
        var output = player.Select(1010)!;
        Assert.Equal(100, output.Large);
        Assert.Equal(8, output.Triggers!.L2.Strength);
        Assert.Equal(TriggerEffect.Off, output.Triggers.R2);
        player.Reset();
        Assert.Null(player.Select(5000));
    }

    [Fact]
    public void InvalidFeedbackCannotOverwriteAcceptedState()
    {
        var player = new ImpulsePlayback();
        player.Accept(Impulse(.5f, .5f), 1000);
        player.Accept(Impulse(0, 0) with { IsValid = false }, 1001);
        Assert.Equal(4, player.Select(1001)!.Triggers!.L2.Strength);
        Assert.Equal(TriggerEffect.Off, ImpulsePlayback.Map(float.NaN));
        Assert.Equal(1, ImpulsePlayback.Map(.001f).Strength);
    }
}
