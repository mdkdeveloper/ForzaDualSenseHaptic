using ForzaHaptics.Gui.Services;

namespace ForzaHaptics.Tests;

public sealed class ProfileStoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"ActiveProfile\":\"Default\"}")]
    public void AutoStartDefaultsToTrueForNewAndLegacySettings(string? settings)
    {
        using var folder = new ProfileTestDirectory();
        if (settings != null)
            File.WriteAllText(Path.Combine(folder.Path, "ForzaHaptics.settings.json"), settings);

        var store = ProfileStore.Open(folder.Path);

        Assert.True(store.AutoStartListening);
        Assert.False(store.AutoStartXboxEmulation);
    }

    [Fact]
    public void XboxAutostartPersistsIndependentlyAcrossProfilesAndListeningChanges()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        using var session = new ProfileSession(store, store.OpenProfile("Default"), "Default");
        long revision = session.Revision;
        session.AutoStartXboxEmulation = true;
        session.AutoStartListening = false;
        session.SwitchTo("profile_1");
        Assert.True(session.AutoStartXboxEmulation);
        session.SwitchTo("Default");
        Assert.True(session.AutoStartXboxEmulation);
        var reopened = ProfileStore.Open(folder.Path);
        Assert.True(reopened.AutoStartXboxEmulation);
        Assert.False(reopened.AutoStartListening);
        reopened.AutoStartListening = true;
        Assert.True(ProfileStore.Open(folder.Path).AutoStartXboxEmulation);
        reopened.AutoStartXboxEmulation = false;
        Assert.False(ProfileStore.Open(folder.Path).AutoStartXboxEmulation);
    }
    [Fact]
    public void AutoStartAndActiveProfilePersistWithoutOverwritingEachOther()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        store.SaveActive("Default");
        store.AutoStartListening = false;

        var reopened = ProfileStore.Open(folder.Path);
        Assert.False(reopened.AutoStartListening);
        Assert.Equal("Default", reopened.ResolveActive());
        reopened.SaveActive("profile_1");

        reopened = ProfileStore.Open(folder.Path);
        Assert.False(reopened.AutoStartListening);
        Assert.Equal("profile_1", reopened.ResolveActive());
        reopened.AutoStartListening = true;
        Assert.True(ProfileStore.Open(folder.Path).AutoStartListening);
        Assert.Equal("profile_1", ProfileStore.Open(folder.Path).ResolveActive());
    }

    [Fact]
    public void SessionAutoStartIsIndependentOfReadOnlyProfileAndConfigRevision()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        using var session = new ProfileSession(store, store.OpenProfile("Default"), "Default");
        long revision = session.Revision;

        session.AutoStartListening = false;

        Assert.True(session.IsReadOnly);
        Assert.Equal(revision, session.Revision);
        Assert.False(ProfileStore.Open(folder.Path).AutoStartListening);
        session.SwitchTo("profile_1");
        Assert.False(session.AutoStartListening);
        session.SwitchTo("Default");
        Assert.False(session.AutoStartListening);
    }

    [Fact]
    public void UnwritableSettingsLogsAutoStartSaveFailure()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        Directory.CreateDirectory(Path.Combine(folder.Path, "ForzaHaptics.settings.json"));
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Capture(ForzaHaptics.Util.LogLevel level, string message) => messages.Enqueue(message);
        ForzaHaptics.Util.Log.Message += Capture;
        try
        {
            store.AutoStartListening = false;
            Assert.Contains(messages, message => message.Contains("Could not remember the auto-start listening preference"));
        }
        finally
        {
            ForzaHaptics.Util.Log.Message -= Capture;
        }
    }

    [Fact]
    public void FirstLaunchUsesBundledProfileAndNeverImportsRootConfig()
    {
        using var folder = new ProfileTestDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "config.json"), ConfigManager.Serialize(new AppConfig { MasterGain = 3.7f }));
        var store = ProfileStore.Open(folder.Path);
        Assert.Equal(new[] { "Default", "profile_1" }, store.List());
        Assert.False(File.Exists(Path.Combine(store.Directory, "Default.json")));
        Assert.Throws<InvalidOperationException>(() => store.PathOf("Default"));
        using var manager = store.OpenActive(out string name);
        Assert.Equal("profile_1", name);
        Assert.False(manager.IsReadOnly);
        Assert.Equal(ConfigManager.Serialize(new AppConfig()), ConfigManager.Serialize(manager.Current));
        manager.Apply(new AppConfig { MasterGain = 0.31f });
        manager.Save();
        var reopened = ProfileStore.Open(folder.Path);
        using var editable = reopened.OpenProfile("profile_1");
        Assert.Equal(0.31f, editable.Current.MasterGain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDefaultIsMovedByteExactlyAndSelectionFollowsIt(bool existingProfile)
    {
        using var folder = new ProfileTestDirectory();
        string profiles = Path.Combine(folder.Path, "Configs");
        Directory.CreateDirectory(profiles);
        byte[] legacy = System.Text.Encoding.UTF8.GetBytes("// preserved comment\r\n{ \"MasterGain\": 0.6 }\r\n");
        File.WriteAllBytes(Path.Combine(profiles, "dEfAuLt.json"), legacy);
        File.WriteAllText(Path.Combine(folder.Path, "ForzaHaptics.settings.json"), "{\"ActiveProfile\":\"DEFAULT\"}");
        if (existingProfile)
            File.WriteAllText(Path.Combine(profiles, "profile_1.json"), "{\"MasterGain\":0.2}");
        var store = ProfileStore.Open(folder.Path);
        string expected = existingProfile ? "Default_imported_1" : "profile_1";
        Assert.Equal(legacy, File.ReadAllBytes(store.PathOf(expected)));
        Assert.False(File.Exists(Path.Combine(profiles, "dEfAuLt.json")));
        using var manager = store.OpenActive(out string name);
        Assert.Equal(expected, name);
        Assert.Equal(0.6f, manager.Current.MasterGain);
    }

    [Fact]
    public void LockedLegacyDefaultDoesNotPreventStartupOrLoseOriginalBytes()
    {
        using var folder = new ProfileTestDirectory();
        string profiles = Path.Combine(folder.Path, "Configs");
        Directory.CreateDirectory(profiles);
        string legacyPath = Path.Combine(profiles, "Default.json");
        byte[] legacy = System.Text.Encoding.UTF8.GetBytes("// locked legacy\r\n{\"MasterGain\":0.6}\r\n");
        File.WriteAllBytes(legacyPath, legacy);
        File.WriteAllText(Path.Combine(folder.Path, "ForzaHaptics.settings.json"), "{\"ActiveProfile\":\"Default\"}");
        using (var held = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var store = ProfileStore.Open(folder.Path);
            Assert.True(File.Exists(legacyPath));
            Assert.True(store.Exists("profile_1"));
            using var active = store.OpenActive(out string name);
            Assert.Equal("Default", name);
            Assert.True(active.IsReadOnly);
        }
        Assert.Equal(legacy, File.ReadAllBytes(legacyPath));
        // A later launch can finish the deferred migration without replacing the seeded profile.
        var reopened = ProfileStore.Open(folder.Path);
        Assert.Equal(legacy, File.ReadAllBytes(reopened.PathOf("Default_imported_1")));
        Assert.False(File.Exists(legacyPath));
        using var migrated = reopened.OpenActive(out string migratedName);
        Assert.Equal("Default_imported_1", migratedName);
    }

    [Fact]
    public void MigrationDoesNotOverwriteEarlierImportsOrChangeUnrelatedSelection()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        store.Create("Default_imported_1", new AppConfig());
        store.Create("custom", new AppConfig());
        store.SaveActive("custom");
        File.WriteAllText(Path.Combine(store.Directory, "Default.json"), "{broken but preserved}");
        store = ProfileStore.Open(folder.Path);
        Assert.Equal("{broken but preserved}", File.ReadAllText(store.PathOf("Default_imported_2")));
        using var manager = store.OpenActive(out string name);
        Assert.Equal("custom", name);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("../outside")]
    [InlineData("broken")]
    public void UnusableSavedProfileFallsBackToProfileOne(string saved)
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        File.WriteAllText(store.PathOf("broken"), "not JSON");
        store.SaveActive(saved);
        using var manager = store.OpenActive(out string name);
        Assert.Equal("profile_1", name);
        Assert.False(manager.IsReadOnly);
    }

    [Fact]
    public void InvalidProfileOneFallsBackToDefaultWithoutOverwritingCorruptData()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        File.WriteAllText(store.PathOf("profile_1"), "invalid");
        store.SaveActive("profile_1");
        using var manager = store.OpenActive(out string name);
        Assert.Equal("Default", name);
        Assert.True(manager.IsReadOnly);
        Assert.Equal("invalid", File.ReadAllText(store.PathOf("profile_1")));
    }

    [Fact]
    public void DefaultCannotBeModifiedButCanBeDuplicatedAndSelected()
    {
        using var folder = new ProfileTestDirectory();
        var store = ProfileStore.Open(folder.Path);
        Assert.NotNull(store.CheckNewName("DEFAULT"));
        Assert.Throws<InvalidOperationException>(() => store.Create("default", new AppConfig()));
        Assert.Throws<InvalidOperationException>(() => store.Rename("Default", "renamed"));
        Assert.Throws<InvalidOperationException>(() => store.Delete("Default"));
        Assert.Throws<InvalidOperationException>(() => store.Duplicate("profile_1", "Default"));
        store.Duplicate("Default", "copy");
        using var copy = store.OpenProfile("copy");
        Assert.False(copy.IsReadOnly);
        Assert.Equal(ConfigManager.Serialize(new AppConfig()), ConfigManager.Serialize(copy.Current));
        using var manager = store.OpenProfile("profile_1");
        using var session = new ProfileSession(store, manager, "profile_1");
        session.SwitchTo("Default");
        Assert.True(session.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => session.Apply(new AppConfig()));
        Assert.Throws<InvalidOperationException>(() => session.Save());
        using var active = store.OpenActive(out string name);
        Assert.Equal("Default", name);
        Assert.True(active.IsReadOnly);
    }
}
