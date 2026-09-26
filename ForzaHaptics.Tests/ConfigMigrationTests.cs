using System.Reflection;
using ForzaHaptics.Gui.Services;

namespace ForzaHaptics.Tests;

public sealed class ConfigMigrationTests
{
    [Fact]
    public void BundledProfileMatchesEveryFactoryDefault()
    {
        using var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("ForzaHaptics.Configs.profile_1.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var profile = ConfigManager.Parse(reader.ReadToEnd());
        Assert.Equal(ConfigManager.Serialize(new AppConfig()), ConfigManager.Serialize(profile));
        Assert.Equal(4, profile.ConfigVersion);
        Assert.Null(profile.MigrationNotice);
    }

    [Fact]
    public void MissingVersionPreservesLegacyDefaultsInsteadOfApplyingNewPreset()
    {
        var old = ConfigManager.Parse("{}");
        Assert.Equal(4, old.ConfigVersion);
        Assert.False(old.Triggers.Throttle.ResistanceEnabled);
        Assert.Equal(45f / 255f, old.Triggers.Throttle.VibAmpMin);
        Assert.Equal(110f / 255f, old.Triggers.Throttle.VibAmpMax);
        Assert.Equal(0.5f, old.Triggers.Brake.AbsAmpMax);
        Assert.False(old.Triggers.Throttle.LateralSlipEnabled);
        Assert.Equal(0f, old.Triggers.Throttle.PulseDurationMs);
        Assert.Equal(0f, old.Triggers.Brake.PulseDurationMs);
        Assert.True(old.Triggers.GearShift.ThrottleUpshift);
        Assert.True(old.Triggers.Surface.Brake);
        Assert.True(old.Triggers.Collision.Enabled);
        Assert.Contains("wall", old.MigrationNotice);
    }

    [Fact]
    public void MigrationConvertsOnlyLegacyUnitsAndKeepsCustomNumericValues()
    {
        var old = ConfigManager.Parse("""
            {"ConfigVersion":1,"MasterGain":0.8,"Triggers":{
              "Strength":0.42,"Throttle":{"VibAmpMin":51,"VibAmpMax":153,"MaxResistance":4,"VibAttackMs":120},
              "Brake":{"AbsAmpMax":2,"AbsWallZones":8,"AbsWallStrength":7},
              "GearShift":{"Amp":204},"Surface":{"Amp":102,"StripAmp":51},"Collision":{"Amp":0}}}
            """);
        Assert.Equal(0.8f, old.MasterGain);
        Assert.Equal(0.42f, old.Triggers.Strength);
        Assert.Equal(0.2f, old.Triggers.Throttle.VibAmpMin);
        Assert.Equal(0.6f, old.Triggers.Throttle.VibAmpMax);
        Assert.Equal(120f, old.Triggers.Throttle.VibAttackMs);
        Assert.Equal(0.25f, old.Triggers.Brake.AbsAmpMax);
        Assert.Equal(0.8f, old.Triggers.GearShift.Amp);
        Assert.Equal(0.4f, old.Triggers.Surface.Amp);
        Assert.Equal(0.2f, old.Triggers.Surface.StripAmp);
        Assert.Equal(0f, old.Triggers.Collision.Amp);
        Assert.DoesNotContain("AbsWall", ConfigManager.Serialize(old));
        var reparsed = ConfigManager.Parse(ConfigManager.Serialize(old));
        Assert.Null(reparsed.MigrationNotice);
        Assert.Equal(ConfigManager.Serialize(old), ConfigManager.Serialize(reparsed));
    }

    [Theory]
    [InlineData("Off", false, false)]
    [InlineData("Resistance", true, false)]
    [InlineData("Vibration", true, true)]
    public void VersionTwoModesBecomeIndependentPermissions(string mode, bool enabled, bool slip)
    {
        var config = ConfigManager.Parse("{\"ConfigVersion\":2,\"Triggers\":{\"Throttle\":{\"Mode\":\"" + mode + "\",\"MinResistance\":1,\"MaxResistance\":6},\"GearShift\":{\"Throttle\":true,\"Brake\":false}}}");
        Assert.Equal(enabled, config.Triggers.Throttle.Enabled);
        Assert.False(config.Triggers.Throttle.ResistanceEnabled);
        Assert.Equal(slip, config.Triggers.Throttle.SlipEnabled);
        Assert.False(config.Triggers.Throttle.LateralSlipEnabled);
        Assert.True(config.Triggers.GearShift.ThrottleUpshift);
        Assert.True(config.Triggers.GearShift.ThrottleDownshift);
        Assert.False(config.Triggers.GearShift.BrakeDownshift);
        string saved = ConfigManager.Serialize(config);
        Assert.DoesNotContain("\"Mode\"", saved);
        Assert.DoesNotContain("ResistanceSource", saved);
        Assert.DoesNotContain("BoostResistance", saved);
        Assert.Contains("independent", config.MigrationNotice);
    }

    [Theory]
    [InlineData(0, TriggerSlipMode.Continuous)]
    [InlineData(350, TriggerSlipMode.Repeated)]
    public void OldPulsePatternAndNumericParametersSurvive(int duration, TriggerSlipMode mode)
    {
        var config = ConfigManager.Parse("{\"ConfigVersion\":2,\"Triggers\":{\"Throttle\":{\"PulseDurationMs\":" + duration + ",\"VibAmpMin\":0.1,\"VibAmpMax\":0.8,\"VibFreqMin\":42,\"MaxVibration\":70}}}");
        Assert.Equal(mode, config.Triggers.Throttle.SlipMode);
        Assert.Equal(duration, config.Triggers.Throttle.PulseDurationMs);
        Assert.Equal(0.1f, config.Triggers.Throttle.VibAmpMin);
        Assert.Equal(0.8f, config.Triggers.Throttle.VibAmpMax);
        Assert.Equal(42, config.Triggers.Throttle.VibFreqMin);
        Assert.Equal(70, config.Triggers.Throttle.MaxVibration);
    }
    [Fact]
    public void VersionThreeCurveIsRemovedWhileVibrationSettingsAndDiskArePreserved()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "v3.json");
        const string json = """
            {"ConfigVersion":3,"Triggers":{"Throttle":{"Enabled":true,"ResistanceEnabled":true,
              "StartZone":2,"MinResistance":4,"MaxResistance":7,"SlipEnabled":true,"LateralSlipEnabled":false,
              "VibAmpMin":0.4,"VibAmpMax":0.7},"GearShift":{"ThrottleUpshift":false,"Amp":0.9}}}
            """;
        File.WriteAllText(path, json);
        using var manager = ConfigManager.Open(path);
        Assert.False(manager.Current.Triggers.Throttle.ResistanceEnabled);
        Assert.True(manager.Current.Triggers.Throttle.SlipEnabled);
        Assert.False(manager.Current.Triggers.Throttle.LateralSlipEnabled);
        Assert.Equal(0.7f, manager.Current.Triggers.Throttle.VibAmpMax);
        Assert.False(manager.Current.Triggers.GearShift.ThrottleUpshift);
        Assert.Equal(0.9f, manager.Current.Triggers.GearShift.Amp);
        Assert.Contains("removed", manager.Current.MigrationNotice);
        Assert.Equal(json, File.ReadAllText(path));
        manager.Save();
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("Resistance", saved);
        Assert.DoesNotContain("StartZone", saved);
        Assert.Null(ConfigManager.Parse(saved).MigrationNotice);
    }

    [Fact]
    public void CloneDoesNotRunMigrationOrChangeNormalizedValues()
    {
        var config = new AppConfig();
        config.Triggers.Throttle.VibAmpMax = 0.375f;
        var clone = ConfigManager.Clone(config);
        Assert.Equal(ConfigManager.Serialize(config), ConfigManager.Serialize(clone));
        Assert.Null(clone.MigrationNotice);
        Assert.NotSame(config.Triggers, clone.Triggers);
    }

    [Fact]
    public void OpenAndRevertNeverRewriteLegacyProfile()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "old.json");
        const string source = "// preserve comment\r\n{\"Triggers\":{\"Throttle\":{\"VibAmpMax\":127}}}\r\n";
        File.WriteAllText(path, source);
        using var manager = ConfigManager.Open(path);
        manager.Revert();
        Assert.Equal(source, File.ReadAllText(path));
        manager.Save();
        Assert.Contains("\"ConfigVersion\": 4", File.ReadAllText(path));
        Assert.Equal(127f / 255f, ConfigManager.Parse(File.ReadAllText(path)).Triggers.Throttle.VibAmpMax);
    }

    [Fact]
    public void VersionTwoFileRemainsUntouchedUntilExplicitSave()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "v2.json");
        const string json = "// original comment\r\n{\"ConfigVersion\":2,\"Triggers\":{\"Throttle\":{\"Mode\":\"Resistance\"},\"GearShift\":{\"Throttle\":true,\"DurationMs\":40}}}\r\n";
        File.WriteAllText(path, json);
        using var manager = ConfigManager.Open(path);
        Assert.True(manager.Current.Triggers.GearShift.ThrottleUpshift);
        Assert.False(manager.Current.Triggers.Throttle.SlipEnabled);
        Assert.Equal(40f, manager.Current.Triggers.GearShift.DurationMs);
        Assert.Equal(0f, manager.Current.Triggers.GearShift.AttackMs);
        manager.Revert();
        Assert.Equal(json, File.ReadAllText(path));
        manager.Save();
        Assert.Contains("\"ConfigVersion\": 4", File.ReadAllText(path));
    }
    [Theory]
    [InlineData("{\"ConfigVersion\":5}")]
    [InlineData("{\"ConfigVersion\":0}")]
    [InlineData("{\"ConfigVersion\":2,\"Triggers\":{\"Throttle\":{\"VibAmpMax\":45}}}")]
    [InlineData("{\"Triggers\":{\"Collision\":{\"Amp\":256}}}")]
    public void UnsupportedVersionsAndInvalidAmplitudeUnitsAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => ConfigManager.Parse(json));

    [Fact]
    public void RevisionChangesOnlyAfterSuccessfulPublication()
    {
        using var directory = new TemporaryDirectory();
        var store = ProfileStore.Open(directory.Path);
        using var session = new ProfileSession(store, store.OpenProfile("profile_1"), "profile_1");
        long revision = session.Revision;
        var changed = ConfigManager.Clone(session.Current);
        changed.MasterGain = 0.7f;
        session.Apply(changed);
        Assert.Equal(++revision, session.Revision);
        session.Save();
        Assert.Equal(revision, session.Revision);
        session.Revert();
        Assert.Equal(++revision, session.Revision);
        changed.MasterGain = -1;
        Assert.Throws<InvalidDataException>(() => session.Apply(changed));
        Assert.Equal(revision, session.Revision);
        session.SwitchTo("Default");
        Assert.Equal(++revision, session.Revision);
        Assert.Throws<InvalidOperationException>(() => session.Apply(new AppConfig()));
        Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public void SuccessfulReloadAdvancesRevisionButInvalidReloadKeepsLastGoodState()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "profile.json");
        using var manager = ConfigManager.Open(path);
        var reload = typeof(ConfigManager).GetMethod("Reload", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Disable asynchronous watching so this checks publication deterministically.
        typeof(ConfigManager).GetMethod("StopWatching", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, null);
        long revision = manager.Revision;
        File.WriteAllText(path, "{\"ConfigVersion\":2,\"MasterGain\":0.5}");
        reload.Invoke(manager, null);
        Assert.Equal(revision + 1, manager.Revision);
        Assert.Equal(0.5f, manager.Current.MasterGain);
        File.WriteAllText(path, "{\"ConfigVersion\":2,\"MasterGain\":-5}");
        reload.Invoke(manager, null);
        Assert.Equal(revision + 1, manager.Revision);
        Assert.Equal(0.5f, manager.Current.MasterGain);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaConfigTests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
