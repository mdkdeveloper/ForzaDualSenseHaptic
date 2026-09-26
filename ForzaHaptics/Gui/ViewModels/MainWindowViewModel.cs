using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaHaptics.Gui.Services;
using ForzaHaptics.Util;

namespace ForzaHaptics.Gui.ViewModels;

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(LogLevel level, string text)
    {
        Text = $"{DateTime.Now:HH:mm:ss}  {text}";
        Color = level switch
        {
            LogLevel.Ok => "#63D98B",
            LogLevel.Warn => "#FFBE6A",
            LogLevel.Error => "#FF7584",
            _ => "#AAB4C3",
        };
    }

    public string Text { get; }
    public string Color { get; }
}

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 300;

    private readonly IProfileSession _profiles;
    private readonly IHapticEngineFacade _engine;
    private readonly IUserDialogService _dialogs;
    private readonly IPlatformShellService _shell;
    private readonly IUiDispatcher _dispatcher;
    private bool _suppressProfileChange;
    private bool _disposed;

    [ObservableProperty]
    private string _title = "ForzaHaptics";

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _restartPending;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isEngineRunning;

    [ObservableProperty]
    private string _engineStateText = "Stopped";

    [ObservableProperty]
    private bool _isSimulationEnabled;

    [ObservableProperty]
    private bool _isTestEnabled;

    [ObservableProperty]
    private string _startStopText = "Stop";

    [ObservableProperty]
    private string _restartText = "Restart output";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _deviceText = string.Empty;

    [ObservableProperty]
    private float _leftMeter;

    [ObservableProperty]
    private float _rightMeter;

    public MainWindowViewModel(
        IProfileSession profiles,
        IHapticEngineFacade engine,
        IUserDialogService dialogs,
        IPlatformShellService shell,
        IUiDispatcher dispatcher,
        IEnumerable<(LogLevel Level, string Text)>? earlyLog = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Settings = new SettingsViewModel(_profiles.Current);
        Settings.Changed += OnSettingChanged;
        _profiles.ReloadedFromDisk += OnReloadedFromDisk;

        if (earlyLog is not null)
        {
            foreach ((LogLevel level, string text) in earlyLog)
                AddLog(level, text);
        }
        Log.Message += OnLog;

        FillProfiles();
        SetDirty(false);
        RefreshStatus();
    }

    public SettingsViewModel Settings { get; }
    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
    public bool ControlsEnabled => !IsBusy;
    public bool CanDeleteProfile => Profiles.Count > 1 && !IsBusy;
    public bool IsEngineStopped => !IsEngineRunning;

    public async Task InitializeAsync() => await StartEngineAsync();

    public void RefreshStatus()
    {
        IsEngineRunning = _engine.IsRunning;
        EngineStateText = IsBusy ? "Working…" : IsEngineRunning ? "Running" : "Stopped";
        StatusText = IsBusy ? "…" : _engine.BuildStatus();
        LeftMeter = IsEngineRunning ? _engine.PeakLeft : 0;
        RightMeter = IsEngineRunning ? _engine.PeakRight : 0;

        string device = IsEngineRunning ? _engine.OutputDescription : "Output is not running";
        if (_engine.HasTriggers)
            device += " | " + _engine.TriggerState;
        DeviceText = device;
        StartStopText = IsEngineRunning ? "Stop" : "Start";
        RestartText = RestartPending ? "Restart output ⟳" : "Restart output";
    }

    public async Task<bool> ConfirmCloseAsync() => await ConfirmLeaveAsync();

    partial void OnSelectedProfileChanged(string? oldValue, string? newValue)
    {
        if (_suppressProfileChange || string.IsNullOrWhiteSpace(newValue) || newValue == _profiles.ActiveProfile)
            return;
        _ = SwitchProfileFromSelectionAsync(newValue);
    }

    partial void OnIsSimulationEnabledChanged(bool value)
    {
        if (value)
            IsTestEnabled = false;
    }

    partial void OnIsTestEnabledChanged(bool value)
    {
        if (value)
            IsSimulationEnabled = false;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ControlsEnabled));
        OnPropertyChanged(nameof(CanDeleteProfile));
        RefreshStatus();
    }

    partial void OnIsEngineRunningChanged(bool value) => OnPropertyChanged(nameof(IsEngineStopped));

    partial void OnRestartPendingChanged(bool value) => RefreshStatus();

    [RelayCommand]
    private async Task StartStopAsync()
    {
        if (IsBusy)
            return;
        if (_engine.IsRunning)
            await RunEngineAsync(() => _engine.StopAsync());
        else
            await StartEngineAsync();
    }

    [RelayCommand]
    private async Task ApplyModesAsync()
    {
        if (!IsBusy)
            await StartEngineAsync();
    }

    [RelayCommand]
    private async Task RestartOutputAsync()
    {
        if (!IsBusy)
            await StartEngineAsync();
    }

    [RelayCommand]
    private void Save()
    {
        if (Settings.HasValidationErrors)
        {
            Log.Warn("Fix invalid settings before saving.");
            return;
        }

        try
        {
            _profiles.Save();
            Settings.MarkSaved();
            SetDirty(false);
            Log.Ok($"Profile '{_profiles.ActiveProfile}' saved.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to save the profile: {exception.Message}");
        }
    }

    [RelayCommand]
    private void Revert()
    {
        try
        {
            AppConfig before = _profiles.Current;
            _profiles.Revert();
            Settings.Load(_profiles.Current);
            if (NeedsRestart(before, _profiles.Current))
                RestartPending = true;
            SetDirty(false);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to reload the profile: {exception.Message}");
        }
    }

    [RelayCommand]
    private void RefreshProfiles()
    {
        _profiles.Refresh();
        FillProfiles();
        Log.Info($"Profiles in the folder: {Profiles.Count} ({string.Join(", ", Profiles)})");
        if (Profiles.Contains(_profiles.ActiveProfile))
            return;

        Log.Warn($"Profile '{_profiles.ActiveProfile}' disappeared; switching profiles.");
        SetDirty(false);
        if (Profiles.Count > 0)
            _ = SwitchProfileAsync(Profiles[0]);
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        if (!await ConfirmLeaveAsync())
            return;
        string? name = await RequestProfileNameAsync("New profile", "New");
        if (name is null)
            return;
        await RunProfileActionAsync(
            () => _profiles.Create(name, new AppConfig()),
            name,
            $"Created profile '{name}' with default settings.");
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync()
    {
        if (!await ConfirmLeaveAsync())
            return;
        string source = _profiles.ActiveProfile;
        string? name = await RequestProfileNameAsync("Copy profile", source + " (copy)");
        if (name is null)
            return;
        await RunProfileActionAsync(
            () => _profiles.Duplicate(source, name),
            name,
            $"Copied profile '{source}' as '{name}'.");
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (!await ConfirmLeaveAsync())
            return;
        string oldName = _profiles.ActiveProfile;
        string? name = await RequestProfileNameAsync("Rename profile", oldName);
        if (name is null)
            return;

        try
        {
            _profiles.Rename(name);
            FillProfiles();
            SetDirty(false);
            Log.Ok($"Renamed profile '{oldName}' to '{name}'.");
        }
        catch (Exception exception)
        {
            Log.Error(exception.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (!CanDeleteProfile || !await _dialogs.ConfirmProfileDeletionAsync(_profiles.ActiveProfile))
            return;

        string oldName = _profiles.ActiveProfile;
        try
        {
            _profiles.Delete(oldName);
            Log.Info($"Profile '{oldName}' was moved to the Recycle Bin.");
            SetDirty(false);
            await SwitchProfileAsync(_profiles.Profiles[0]);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to delete the profile: {exception.Message}");
        }
    }

    [RelayCommand]
    private void OpenProfilesFolder()
    {
        try
        {
            _shell.OpenDirectory(_profiles.ProfileDirectory);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to open the profiles folder: {exception.Message}");
        }
    }

    private EngineOptions CurrentOptions() => new()
    {
        Simulate = IsSimulationEnabled,
        Test = IsTestEnabled,
    };

    private async Task StartEngineAsync()
    {
        bool started = false;
        await RunEngineAsync(async () => started = await _engine.StartAsync(CurrentOptions()));
        if (started)
        {
            RestartPending = false;
            Settings.MarkOutputRestarted();
        }
    }

    private async Task RunEngineAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Log.Error($"Error: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs eventArgs)
    {
        try
        {
            _profiles.Apply(Settings.Working);
        }
        catch (Exception exception)
        {
            Log.Warn($"The value was not applied: {exception.Message}");
            return;
        }

        if (eventArgs.RequiresRestart)
            RestartPending = true;
        SetDirty(true);
    }

    private void OnReloadedFromDisk(object? sender, EventArgs eventArgs) => _dispatcher.Post(() =>
    {
        if (IsDirty)
        {
            try { _profiles.Apply(Settings.Working); } catch { /* Keep the last valid settings. */ }
            Log.Warn("The profile file changed externally; unsaved window changes take precedence.");
            return;
        }
        Settings.Load(_profiles.Current);
    });

    private async Task SwitchProfileFromSelectionAsync(string profileName)
    {
        if (!await ConfirmLeaveAsync())
        {
            SelectProfile(_profiles.ActiveProfile);
            return;
        }
        await SwitchProfileAsync(profileName);
    }

    private async Task SwitchProfileAsync(string profileName)
    {
        AppConfig before = _profiles.Current;
        try
        {
            _profiles.SwitchTo(profileName);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to open profile '{profileName}': {exception.Message}");
            FillProfiles();
            return;
        }

        Settings.Load(_profiles.Current);
        SetDirty(false);
        FillProfiles();
        if (_engine.IsRunning && NeedsRestart(before, _profiles.Current))
            await StartEngineAsync();
    }

    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsDirty)
            return true;

        UnsavedChangesDecision decision = await _dialogs.ConfirmUnsavedChangesAsync(_profiles.ActiveProfile);
        switch (decision)
        {
            case UnsavedChangesDecision.Save:
                Save();
                return !IsDirty;
            case UnsavedChangesDecision.Discard:
                Revert();
                return true;
            default:
                return false;
        }
    }

    private async Task<string?> RequestProfileNameAsync(string title, string initialValue)
    {
        while (true)
        {
            string? name = await _dialogs.RequestTextAsync(title, "Profile name:", initialValue);
            if (name is null)
                return null;
            name = name.Trim();
            string? error = _profiles.ValidateNewName(name);
            if (error is null)
                return name;
            await _dialogs.ShowWarningAsync(error);
            initialValue = name;
        }
    }

    private async Task RunProfileActionAsync(Action action, string switchTo, string successMessage)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Log.Error(exception.Message);
            return;
        }
        Log.Ok(successMessage);
        await SwitchProfileAsync(switchTo);
    }

    private void FillProfiles()
    {
        _suppressProfileChange = true;
        try
        {
            Profiles.Clear();
            foreach (string profile in _profiles.Profiles)
                Profiles.Add(profile);
            SelectedProfile = _profiles.ActiveProfile;
        }
        finally
        {
            _suppressProfileChange = false;
        }
        OnPropertyChanged(nameof(CanDeleteProfile));
    }

    private void SelectProfile(string profileName)
    {
        _suppressProfileChange = true;
        try { SelectedProfile = profileName; }
        finally { _suppressProfileChange = false; }
    }

    private void SetDirty(bool value)
    {
        IsDirty = value;
        Title = $"ForzaHaptics — {_profiles.ActiveProfile}{(value ? " *" : string.Empty)}";
    }

    private static bool NeedsRestart(AppConfig left, AppConfig right) =>
        left.Port != right.Port || left.Output != right.Output || left.UsbLatencyMs != right.UsbLatencyMs ||
        !left.UsbHapticChannels.SequenceEqual(right.UsbHapticChannels) ||
        !left.ForwardTo.SequenceEqual(right.ForwardTo) ||
        left.Triggers.Enabled != right.Triggers.Enabled;

    private void OnLog(LogLevel level, string text) => _dispatcher.Post(() => AddLog(level, text));

    private void AddLog(LogLevel level, string text)
    {
        LogEntries.Add(new LogEntryViewModel(level, text));
        while (LogEntries.Count > MaxLogLines)
            LogEntries.RemoveAt(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Log.Message -= OnLog;
        _profiles.ReloadedFromDisk -= OnReloadedFromDisk;
        Settings.Changed -= OnSettingChanged;
        _engine.Dispose();
        _profiles.Dispose();
    }
}
