namespace ForzaHaptics.Tests;

public sealed class ConfigManagerTests
{
    [Fact]
    public void DefaultIsImmutableIncludingNestedValues()
    {
        using var manager = ConfigManager.OpenDefault();
        Assert.True(manager.IsReadOnly);
        Assert.Null(manager.Path);
        var exposed = manager.Current;
        exposed.MasterGain = 4;
        exposed.Triggers.Throttle.Intensity = 99;
        exposed.UsbHapticChannels[0] = 99;
        exposed.ForwardTo.Add("127.0.0.1:1234");
        Assert.Equal(ConfigManager.Serialize(new AppConfig()), ConfigManager.Serialize(manager.Current));
        Assert.Throws<InvalidOperationException>(() => manager.Apply(new AppConfig()));
        Assert.Throws<InvalidOperationException>(() => manager.Save());
        manager.Revert();
        Assert.Equal(ConfigManager.Serialize(new AppConfig()), ConfigManager.Serialize(manager.Current));
    }

    [Fact]
    public async Task SwitchingProfilesIsolatesDefaultAndReloadsTheNewFile()
    {
        using var folder = new ProfileTestDirectory();
        string oldPath = System.IO.Path.Combine(folder.Path, "old.json");
        string newPath = System.IO.Path.Combine(folder.Path, "new.json");
        File.WriteAllText(newPath, ConfigManager.Serialize(new AppConfig { MasterGain = 0.4f }));
        using var manager = ConfigManager.Open(oldPath);
        manager.Save(); // The one-second self-write suppression must not follow a profile switch.
        manager.SwitchToDefault();
        int reloads = 0;
        manager.ReloadedFromDisk += () => Interlocked.Increment(ref reloads);
        File.WriteAllText(oldPath, ConfigManager.Serialize(new AppConfig { MasterGain = 0.9f }));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.True(manager.IsReadOnly);
        Assert.Equal(new AppConfig().MasterGain, manager.Current.MasterGain);
        Assert.Equal(0, Volatile.Read(ref reloads));
        manager.SwitchTo(newPath);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ReloadedFromDisk += () => reloaded.TrySetResult();
        File.WriteAllText(newPath, ConfigManager.Serialize(new AppConfig { MasterGain = 0.8f }));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0.8f, manager.Current.MasterGain);
        File.WriteAllText(oldPath, ConfigManager.Serialize(new AppConfig { MasterGain = 0.1f }));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(0.8f, manager.Current.MasterGain);
    }

    [Fact]
    public void EditableProfileCanSaveRevertAndSwitchToDefault()
    {
        using var folder = new ProfileTestDirectory();
        string path = System.IO.Path.Combine(folder.Path, "custom.json");
        using var manager = ConfigManager.Open(path);
        var config = manager.Current;
        config.MasterGain = 0.7f;
        manager.Apply(config);
        manager.Save();
        Assert.Equal(0.7f, ConfigManager.Parse(File.ReadAllText(path)).MasterGain);
        manager.Apply(new AppConfig { MasterGain = 0.3f });
        manager.Revert();
        Assert.Equal(0.7f, manager.Current.MasterGain);
        manager.SwitchToDefault();
        Assert.True(manager.IsReadOnly);
        Assert.Null(manager.Path);
        File.WriteAllText(path, ConfigManager.Serialize(new AppConfig { MasterGain = 0.2f }));
        Assert.Equal(new AppConfig().MasterGain, manager.Current.MasterGain);
        manager.SwitchTo(path);
        Assert.False(manager.IsReadOnly);
        Assert.Equal(0.2f, manager.Current.MasterGain);
    }
}

internal sealed class ProfileTestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaHaptics-profile-tests-" + Guid.NewGuid().ToString("N"));
    public ProfileTestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
