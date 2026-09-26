using ForzaHaptics.Gui.Services;
using ForzaHaptics.Gui.ViewModels;

namespace ForzaHaptics.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void SettingChangesApplyLiveAndSaveClearsDirtyState()
    {
        var profiles = new FakeProfileSession();
        using var viewModel = CreateViewModel(profiles: profiles);
        var port = Assert.IsType<TextSettingFieldViewModel>(Field(viewModel.Settings, "Connection", nameof(AppConfig.Port)));

        port.Text = "5400";
        Assert.True(port.TryCommitText());

        Assert.Equal(5400, profiles.Current.Port);
        Assert.True(viewModel.IsDirty);
        Assert.EndsWith(" *", viewModel.Title);

        viewModel.SaveCommand.Execute(null);

        Assert.Equal(1, profiles.SaveCount);
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.Title.EndsWith(" *", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloseHonorsCancelAndDiscardDecisions()
    {
        var profiles = new FakeProfileSession();
        var dialogs = new FakeDialogService { UnsavedDecision = UnsavedChangesDecision.Cancel };
        using var viewModel = CreateViewModel(profiles, dialogs: dialogs);
        var port = Assert.IsType<TextSettingFieldViewModel>(Field(viewModel.Settings, "Connection", nameof(AppConfig.Port)));
        port.Text = "5400";
        Assert.True(port.TryCommitText());

        Assert.False(await viewModel.ConfirmCloseAsync());
        Assert.True(viewModel.IsDirty);

        dialogs.UnsavedDecision = UnsavedChangesDecision.Discard;
        Assert.True(await viewModel.ConfirmCloseAsync());
        Assert.Equal(1, profiles.RevertCount);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task InitializeAndModeChangesRestartEngineWithCurrentOptions()
    {
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(engine: engine);

        Assert.False(viewModel.IsEngineRunning);
        Assert.True(viewModel.IsEngineStopped);

        viewModel.IsSimulationEnabled = true;
        await viewModel.InitializeAsync();

        Assert.Equal(1, engine.StartCount);
        Assert.True(engine.LastOptions?.Simulate);
        Assert.False(engine.LastOptions?.Test);
        Assert.True(viewModel.IsEngineRunning);
        Assert.False(viewModel.IsEngineStopped);
        Assert.False(viewModel.RestartPending);
        Assert.Equal("Stop", viewModel.StartStopText);

        await viewModel.StartStopCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.StopCount);
        Assert.False(viewModel.IsEngineRunning);
        Assert.True(viewModel.IsEngineStopped);
        Assert.Equal("Start", viewModel.StartStopText);

        viewModel.IsTestEnabled = true;
        await viewModel.ApplyModesCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSimulationEnabled);
        Assert.True(viewModel.IsTestEnabled);
        Assert.Equal(2, engine.StartCount);
        Assert.True(engine.LastOptions?.Test);
    }

    private static MainWindowViewModel CreateViewModel(
        FakeProfileSession? profiles = null,
        FakeEngineFacade? engine = null,
        FakeDialogService? dialogs = null) => new(
            profiles ?? new FakeProfileSession(),
            engine ?? new FakeEngineFacade(),
            dialogs ?? new FakeDialogService(),
            new FakeShellService(),
            new ImmediateDispatcher());

    private static SettingFieldViewModel Field(SettingsViewModel settings, string groupTitle, string propertyName) =>
        settings.Tabs.SelectMany(tab => tab.Groups)
            .Single(group => group.Title == groupTitle)
            .Fields.Single(item => item.PropertyName == propertyName);

    private sealed class FakeProfileSession : IProfileSession
    {
        private AppConfig _saved = new();

        public IReadOnlyList<string> Profiles { get; private set; } = new[] { "Default" };
        public string ActiveProfile { get; private set; } = "Default";
        public string ProfileDirectory => "C:\\Profiles";
        public AppConfig Current { get; private set; } = new();
        public int SaveCount { get; private set; }
        public int RevertCount { get; private set; }
        public event EventHandler? ReloadedFromDisk;

        public void Refresh() { }
        public void Apply(AppConfig config) => Current = ConfigManager.Clone(config);
        public void Save()
        {
            SaveCount++;
            _saved = ConfigManager.Clone(Current);
        }
        public void Revert()
        {
            RevertCount++;
            Current = ConfigManager.Clone(_saved);
        }
        public void SwitchTo(string profileName) => ActiveProfile = profileName;
        public string? ValidateNewName(string? profileName) => null;
        public void Create(string profileName, AppConfig config) => Profiles = Profiles.Append(profileName).ToArray();
        public void Duplicate(string sourceProfileName, string profileName) => Profiles = Profiles.Append(profileName).ToArray();
        public void Rename(string profileName) => ActiveProfile = profileName;
        public void Delete(string profileName) => Profiles = Profiles.Where(item => item != profileName).ToArray();
        public void Dispose() { }
        public void RaiseReloaded() => ReloadedFromDisk?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeEngineFacade : IHapticEngineFacade
    {
        public bool IsRunning { get; private set; }
        public string OutputDescription => "Fake output";
        public string TriggersDescription => string.Empty;
        public bool HasTriggers => false;
        public string TriggerState => string.Empty;
        public float PeakLeft => 0.25f;
        public float PeakRight => 0.5f;
        public string ActiveEffects => string.Empty;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public EngineOptions? LastOptions { get; private set; }

        public Task<bool> StartAsync(EngineOptions options, CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastOptions = options;
            IsRunning = true;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public string BuildStatus() => IsRunning ? "Running" : "Stopped";
        public void Dispose() { }
    }

    private sealed class FakeDialogService : IUserDialogService
    {
        public UnsavedChangesDecision UnsavedDecision { get; set; } = UnsavedChangesDecision.Save;

        public Task<string?> RequestTextAsync(string title, string prompt, string initialValue, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(initialValue);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string profileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnsavedDecision);
        public Task<bool> ConfirmProfileDeletionAsync(string profileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task ShowWarningAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowErrorAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeShellService : IPlatformShellService
    {
        public void OpenDirectory(string path) { }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
