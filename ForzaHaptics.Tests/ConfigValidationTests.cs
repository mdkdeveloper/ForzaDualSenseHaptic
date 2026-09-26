namespace ForzaHaptics.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public void FactoryConfigurationIsValid() => new AppConfig().Validate();

    public static IEnumerable<object[]> InvalidConfigurations()
    {
        yield return new object[] { "nonfinite road", (Action<AppConfig>)(c => c.Road.Gain = float.NaN) };
        yield return new object[] { "nonfinite trigger", (Action<AppConfig>)(c => c.Triggers.Throttle.GripLoss = float.PositiveInfinity) };
        yield return new object[] { "inverted band", (Action<AppConfig>)(c => { c.LowCutHz = 100; c.HighCutHz = 80; }) };
        yield return new object[] { "inverted road", (Action<AppConfig>)(c => c.Road.MinFreqHz = 200) };
        yield return new object[] { "inverted amplitude", (Action<AppConfig>)(c => c.Triggers.Throttle.VibAmpMin = 0.6f) };
        yield return new object[] { "inverted trigger frequency", (Action<AppConfig>)(c => c.Triggers.Brake.MinVibration = 50) };
        yield return new object[] { "inverted slip", (Action<AppConfig>)(c => c.Slip.Full = c.Slip.Start - 0.1f) };
        yield return new object[] { "inverted impact", (Action<AppConfig>)(c => c.Impact.FullG = c.Impact.StartG - 0.1f) };
        yield return new object[] { "invalid enum", (Action<AppConfig>)(c => c.Triggers.Throttle.SlipMode = (TriggerSlipMode)99) };
        yield return new object[] { "negative burst", (Action<AppConfig>)(c => c.Triggers.Collision.DurationMs = -1) };
        yield return new object[] { "overflow amplitude", (Action<AppConfig>)(c => c.Triggers.Surface.StripAmp = 1.1f) };
        yield return new object[] { "envelope longer than burst", (Action<AppConfig>)(c => { c.Triggers.Brake.SlipMode = TriggerSlipMode.Repeated; c.Triggers.Brake.PulseDurationMs = 100; }) };
        yield return new object[] { "duplicate channels", (Action<AppConfig>)(c => c.UsbHapticChannels = [2, 2]) };
        yield return new object[] { "negative channel", (Action<AppConfig>)(c => c.UsbHapticChannels = [-1, 3]) };
        yield return new object[] { "invalid forward", (Action<AppConfig>)(c => c.ForwardTo = ["nonsense"]) };
        yield return new object[] { "invalid output", (Action<AppConfig>)(c => c.Output = "invalid") };
        yield return new object[] { "null section", (Action<AppConfig>)(c => c.Triggers = null!) };
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void InvalidValuesAreRejectedInsteadOfSilentlyClamped(string description, Action<AppConfig> change)
    {
        Assert.NotEmpty(description);
        var config = new AppConfig();
        change(config);
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void LegacyContinuousPulseAllowsLongAttackWithoutInventingABurst()
    {
        var config = new AppConfig();
        config.Triggers.Throttle.PulseDurationMs = 0;
        config.Triggers.Throttle.VibAttackMs = 2000;
        config.Validate();
    }
}
