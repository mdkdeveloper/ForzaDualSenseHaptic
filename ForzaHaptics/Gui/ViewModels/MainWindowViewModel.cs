using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaHaptics.Controllers;
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
    private readonly IControllerService? _controller;
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
    private bool _isTriggerTestEnabled;

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

    [ObservableProperty]
    private string _controllerName = "DualSense";

    [ObservableProperty]
    private bool _isControllerConnected;

    [ObservableProperty]
    private bool _isBluetoothConnected;

    [ObservableProperty]
    private bool _isUsbConnected;

    [ObservableProperty]
    private int? _batteryPercent;

    [ObservableProperty]
    private bool _isControllerCharging;

    [ObservableProperty]
    private bool _isDisconnecting;

    [ObservableProperty]
    private string _controllerError = string.Empty;

    public string ControllerStatusText => IsControllerConnected ? "Connected" : "Not connected";
    public string BatteryText => BatteryPercent is { } percent ? $"{percent}%" : "—";
    public string BatteryTooltip => !IsControllerConnected ? "Controller is not connected"
        : BatteryPercent is null ? IsBluetoothConnected
            ? "Battery level is unavailable; Bluetooth battery data requires enhanced input reports"
            : "Battery level is unavailable"
        : $"Battery: approximately {BatteryPercent}%{(IsControllerCharging ? " · Charging" : string.Empty)}";
    public bool CanDisconnectController => !IsBusy && !IsDisconnecting &&
        _controller?.Snapshot is { IsConnected: true, CanDisconnect: true, Transport: ControllerTransport.Bluetooth, DeviceId: not null };
    public string DisconnectTooltip => IsDisconnecting ? "Disconnecting controller…"
        : IsUsbConnected ? "USB supplies power; unplug the cable to disconnect"
        : !IsControllerConnected ? "Controller is not connected"
        : _controller?.Snapshot.CanDisconnect != true ? "Bluetooth device address is unavailable"
        : "Turn off controller";

    public MainWindowViewModel(
        IProfileSession profiles,
        IHapticEngineFacade engine,
        IUserDialogService dialogs,
        IPlatformShellService shell,
        IUiDispatcher dispatcher,
        IEnumerable<(LogLevel Level, string Text)>? earlyLog = null,
        IControllerService? controller = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _controller = controller;

        Settings = new SettingsViewModel(_profiles.Current) { IsReadOnly = _profiles.IsReadOnly };
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
    public bool CanEditProfile => !_profiles.IsReadOnly && !IsBusy;
    public bool CanDeleteProfile => Profiles.Count > 1 && CanEditProfile;
    public bool IsEngineStopped => !IsEngineRunning;

    public async Task InitializeAsync() => await StartEngineAsync();

    public void RefreshStatus()
    {
        RefreshControllerStatus();
        IsEngineRunning = _engine.IsRunning;
        EngineStateText = IsBusy ? "Working…" : IsEngineRunning ? "Running" : "Stopped";
        StatusText = IsBusy ? "…" : _engine.BuildStatus();
        LeftMeter = IsEngineRunning ? _engine.PeakLeft : 0;
        RightMeter = IsEngineRunning ? _engine.PeakRight : 0;

        string device = IsEngineRunning ? _engine.OutputDescription : "Output is not running";
        if (_engine.HasTriggers)
            device += " | desired: " + _engine.TriggerState;
        if (_engine.HasTriggers && _controller is not null)
            device += " | " + _controller.Snapshot.PhysicalTriggerText + " | " + _controller.Snapshot.TriggerFeedbackText;
        DeviceText = device;
        StartStopText = IsEngineRunning ? "Stop" : "Start";
        RestartText = RestartPending ? "Restart output ⟳" : "Restart output";
    }

    private void RefreshControllerStatus()
    {
        var state = _controller?.Snapshot;
        IsControllerConnected = state?.IsConnected == true;
        ControllerName = IsControllerConnected && !string.IsNullOrWhiteSpace(state?.Name) ? state.Name : "DualSense";
        IsBluetoothConnected = IsControllerConnected && state?.Transport == ControllerTransport.Bluetooth;
        IsUsbConnected = IsControllerConnected && state?.Transport == ControllerTransport.Usb;
        BatteryPercent = IsControllerConnected ? state?.BatteryPercent : null;
        IsControllerCharging = IsControllerConnected && state?.IsCharging == true;
        OnPropertyChanged(nameof(ControllerStatusText));
        OnPropertyChanged(nameof(BatteryText));
        OnPropertyChanged(nameof(BatteryTooltip));
        OnPropertyChanged(nameof(CanDisconnectController));
        OnPropertyChanged(nameof(DisconnectTooltip));
        DisconnectControllerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectController))]
    private async Task DisconnectControllerAsync()
    {
        if (!CanDisconnectController || _controller?.Snapshot.DeviceId is not { } deviceId)
            return;

        IsDisconnecting = true;
        IsBusy = true;
        ControllerError = string.Empty;
        try
        {
            await _engine.StopAsync();
            await _controller.DisconnectAsync(deviceId);
            Log.Info("Bluetooth controller disconnected. Reconnect it manually when ready.");
        }
        catch (Exception exception)
        {
            ControllerError = $"Could not disconnect controller: {exception.Message}";
            Log.Error(ControllerError);
            await _dialogs.ShowErrorAsync(ControllerError);
        }
        finally
        {
            IsDisconnecting = false;
            IsBusy = false;
            RefreshStatus();
        }
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
        {
            IsTestEnabled = false;
            IsTriggerTestEnabled = false;
        }
    }

    partial void OnIsTestEnabledChanged(bool value)
    {
        if (value)
        {
            IsSimulationEnabled = false;
            IsTriggerTestEnabled = false;
        }
    }

    partial void OnIsTriggerTestEnabledChanged(bool value)
    {
        if (value)
        {
            IsSimulationEnabled = false;
            IsTestEnabled = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ControlsEnabled));
        OnPropertyChanged(nameof(CanDeleteProfile));
        OnPropertyChanged(nameof(CanEditProfile));
        SaveCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private void Save()
    {
        if (!CanEditProfile)
            return;

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
            _ = RecoverProfileAsync();
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

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task RenameProfileAsync()
    {
        if (!CanEditProfile)
            return;

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

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
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
            await RecoverProfileAsync();
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
        TriggerTest = IsTriggerTestEnabled,
        ControllerDeviceId = _controller?.Snapshot.DeviceId,
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
        if (_profiles.IsReadOnly)
            return;

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
        if (IsDirty && !_profiles.IsReadOnly)
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

    private async Task RecoverProfileAsync()
    {
        string? initialProfile = _profiles.Profiles.FirstOrDefault(name =>
            string.Equals(name, ProfileStore.InitialProfileName, StringComparison.OrdinalIgnoreCase));
        if (initialProfile != null && await SwitchProfileAsync(initialProfile))
            return;
        await SwitchProfileAsync(ProfileStore.DefaultName);
    }

    private async Task<bool> SwitchProfileAsync(string profileName)
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
            return false;
        }

        Settings.Load(_profiles.Current);
        SetDirty(false);
        FillProfiles();
        if (_engine.IsRunning && NeedsRestart(before, _profiles.Current))
            await StartEngineAsync();
        return true;
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
        Settings.IsReadOnly = _profiles.IsReadOnly;
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
        OnPropertyChanged(nameof(CanEditProfile));
        SaveCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
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
        _controller?.Dispose();
        _profiles.Dispose();
    }
}
