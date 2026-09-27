using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ForzaHaptics.Controllers;
using ForzaHaptics.Emulation;
using ForzaHaptics.Gui.Services;
using ForzaHaptics.Gui.ViewModels;

namespace ForzaHaptics.Tests;

public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData(XboxEmulationState.Starting)]
    [InlineData(XboxEmulationState.Stopping)]
    public async Task TransitionBlocksToggleBackendAndRetryAndDisplaysProgress(XboxEmulationState state)
    {
        var emulation = new FakeXboxEmulationService
        {
            State = state,
            StatusOverride = "Waiting for Windows removal (4.2 s)"
        };
        using var vm = CreateViewModel(emulation: emulation);
        vm.RefreshStatus();
        Assert.False(vm.CanChangeXboxEmulation);
        Assert.False(vm.CanChangeXboxBackend);
        Assert.Equal(emulation.Status, vm.XboxStatus);
        vm.IsXboxEmulationEnabled = true;
        vm.SelectedXboxBackendIndex = 1;
        await vm.CheckXboxDependenciesCommand.ExecuteAsync(null);
        Assert.False(vm.IsXboxEmulationEnabled);
        Assert.Equal(0, vm.SelectedXboxBackendIndex);
        Assert.Empty(emulation.Operations);
        emulation.State = XboxEmulationState.Off;
        vm.RefreshStatus();
        Assert.True(vm.CanChangeXboxEmulation);
    }

    [Fact]
    public async Task BackendRestoredBeforeAutoStartAndCannotChangeWhileEnabled()
    {
        var profiles = new FakeProfileSession { AutoStartListening = false, AutoStartXboxEmulation = true, XboxBackend = XboxBackend.HidMaestro };
        var emulation = new FakeXboxEmulationService();
        using var vm = CreateViewModel(profiles: profiles, emulation: emulation);
        Assert.Equal(1, vm.SelectedXboxBackendIndex);
        await vm.InitializeAsync();
        Assert.Equal(XboxBackend.HidMaestro, emulation.Backend);
        Assert.True(emulation.IsEnabled);
        Assert.Equal(XboxBackend.HidMaestro, emulation.BackendAtEnable);
        Assert.False(vm.CanChangeXboxBackend);
        vm.SelectedXboxBackendIndex = 0;
        Assert.Equal(1, vm.SelectedXboxBackendIndex);
        Assert.Equal(XboxBackend.HidMaestro, profiles.XboxBackend);
    }

    [Fact]
    public async Task BackendCannotChangeDuringInitialization()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new FakeProfileSession { AutoStartListening = false };
        var emulation = new FakeXboxEmulationService { InitializeCompletion = completion.Task };
        using var vm = CreateViewModel(profiles: profiles, emulation: emulation);
        Task initialization = vm.InitializeAsync();
        Assert.False(vm.CanChangeXboxBackend);
        vm.SelectedXboxBackendIndex = 1;
        Assert.Equal(0, vm.SelectedXboxBackendIndex);
        Assert.Equal(XboxBackend.ViGEm, profiles.XboxBackend);
        completion.SetResult();
        await initialization;
        Assert.True(vm.CanChangeXboxBackend);
    }

    [Fact]
    public async Task BackendSelectionPersistsAndInstallationRequiresConfirmation()
    {
        var profiles = new FakeProfileSession { AutoStartListening = false };
        var emulation = new FakeXboxEmulationService();
        var dialogs = new FakeDialogService();
        using var vm = CreateViewModel(profiles: profiles, emulation: emulation, dialogs: dialogs);
        vm.SelectedXboxBackendIndex = 1;
        Assert.Equal(XboxBackend.HidMaestro, profiles.XboxBackend);
        await vm.InstallHidMaestroCommand.ExecuteAsync(null);
        Assert.Equal(0, emulation.InstallCount);
        dialogs.ConfirmInstall = true;
        await vm.InstallHidMaestroCommand.ExecuteAsync(null);
        Assert.Equal(1, emulation.InstallCount);
    }

    [Fact]
    public async Task DefaultCannotBeEditedSavedRenamedOrDeletedButCanBeCopied()
    {
        var profiles = new FakeProfileSession();
        profiles.SwitchTo("Default");
        using var viewModel = CreateViewModel(profiles: profiles);
        Assert.True(viewModel.Settings.IsReadOnly);
        Assert.False(viewModel.CanDeleteProfile);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RenameProfileCommand.CanExecute(null));
        Assert.False(viewModel.DeleteProfileCommand.CanExecute(null));
        Assert.All(viewModel.Settings.Tabs.SelectMany(tab => tab.Groups).SelectMany(group => group.Fields),
            field => Assert.False(field.IsEnabled));
        var port = Assert.IsType<TextSettingFieldViewModel>(Field(viewModel.Settings, "Connection", nameof(AppConfig.Port)));
        port.Text = "5400";
        Assert.False(port.TryCommitText());
        viewModel.SaveCommand.Execute(null);
        await viewModel.RenameProfileCommand.ExecuteAsync(null);
        await viewModel.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Equal("Default", profiles.ActiveProfile);
        Assert.Equal(5310, profiles.Current.Port);
        Assert.Equal(0, profiles.SaveCount);
        Assert.False(viewModel.IsDirty);

        await viewModel.DuplicateProfileCommand.ExecuteAsync(null);
        Assert.Equal("Default (copy)", profiles.ActiveProfile);
        Assert.False(viewModel.Settings.IsReadOnly);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.RenameProfileCommand.CanExecute(null));
        Assert.True(viewModel.CanDeleteProfile);
    }

    [Fact]
    public void SwitchingToDefaultUpdatesEditorAndCommandAvailability()
    {
        var profiles = new FakeProfileSession();
        using var viewModel = CreateViewModel(profiles: profiles);
        Assert.False(viewModel.Settings.IsReadOnly);
        viewModel.SelectedProfile = "Default";
        Assert.True(viewModel.Settings.IsReadOnly);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        viewModel.SelectedProfile = "profile_1";
        Assert.False(viewModel.Settings.IsReadOnly);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, "profile_1")]
    [InlineData(false, "Default")]
    public void RefreshMissingProfilePrefersEditableProfile(bool keepEditable, string expected)
    {
        var profiles = new FakeProfileSession();
        profiles.Create("Missing", new AppConfig());
        profiles.SwitchTo("Missing");
        using var viewModel = CreateViewModel(profiles: profiles);
        profiles.Delete("Missing");
        if (!keepEditable)
            profiles.Delete("profile_1");
        viewModel.RefreshProfilesCommand.Execute(null);
        Assert.Equal(expected, profiles.ActiveProfile);
        Assert.Equal(expected == "Default", viewModel.Settings.IsReadOnly);
    }

    [Fact]
    public void RefreshMissingProfileRecoversInitialProfileCaseInsensitively()
    {
        var profiles = new FakeProfileSession();
        profiles.Delete(ProfileStore.InitialProfileName);
        profiles.Create("PROFILE_1", new AppConfig());
        profiles.Create("Missing", new AppConfig());
        profiles.SwitchTo("Missing");
        using var viewModel = CreateViewModel(profiles: profiles);
        profiles.Delete("Missing");
        viewModel.RefreshProfilesCommand.Execute(null);
        Assert.Equal("PROFILE_1", profiles.ActiveProfile);
        Assert.Equal("PROFILE_1", viewModel.SelectedProfile);
        Assert.False(viewModel.Settings.IsReadOnly);
    }

    [Fact]
    public void RefreshMissingProfileFallsBackToDefaultWhenInitialProfileCannotLoad()
    {
        var profiles = new FakeProfileSession();
        profiles.Create("Missing", new AppConfig());
        profiles.SwitchTo("Missing");
        using var viewModel = CreateViewModel(profiles: profiles);
        profiles.Delete("Missing");
        profiles.UnloadableProfile = ProfileStore.InitialProfileName;
        viewModel.RefreshProfilesCommand.Execute(null);
        Assert.Equal(ProfileStore.DefaultName, profiles.ActiveProfile);
        Assert.True(viewModel.Settings.IsReadOnly);
    }

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

    [Fact]
    public async Task TriggerTestIsExplicitAndExclusiveWithBodyTestAndSimulation()
    {
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(engine: engine);
        Assert.False(viewModel.IsTriggerTestEnabled);
        viewModel.IsTestEnabled = true;
        viewModel.IsTriggerTestEnabled = true;
        Assert.False(viewModel.IsTestEnabled);
        Assert.False(viewModel.IsSimulationEnabled);
        await viewModel.ApplyModesCommand.ExecuteAsync(null);
        Assert.True(engine.LastOptions?.TriggerTest);
        Assert.False(engine.LastOptions?.Test);
        Assert.False(engine.LastOptions?.Simulate);
        viewModel.IsSimulationEnabled = true;
        Assert.False(viewModel.IsTriggerTestEnabled);
        viewModel.IsTriggerTestEnabled = true;
        viewModel.IsTestEnabled = true;
        Assert.False(viewModel.IsTriggerTestEnabled);
    }

    [Fact]
    public void ControllerStatusTracksConnectionWhileEngineIsStopped()
    {
        var controller = new FakeControllerService { Snapshot = Connected(ControllerTransport.Bluetooth, 65) };
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(engine: engine, controller: controller);

        Assert.True(viewModel.IsEngineStopped);
        Assert.Equal(0, engine.StartCount);
        Assert.True(viewModel.IsControllerConnected);
        Assert.Equal("Connected", viewModel.ControllerStatusText);
        Assert.True(viewModel.IsBluetoothConnected);
        Assert.False(viewModel.IsUsbConnected);
        Assert.Equal(65, viewModel.BatteryPercent);
        Assert.Equal("65%", viewModel.BatteryText);
        Assert.True(viewModel.CanDisconnectController);

        controller.Snapshot = ControllerSnapshot.Disconnected;
        viewModel.RefreshStatus();
        Assert.False(viewModel.IsControllerConnected);
        Assert.Equal("Not connected", viewModel.ControllerStatusText);
        Assert.False(viewModel.IsBluetoothConnected);
        Assert.Null(viewModel.BatteryPercent);
        Assert.Equal("—", viewModel.BatteryText);
        Assert.False(viewModel.CanDisconnectController);

        controller.Snapshot = Connected(ControllerTransport.Usb, null) with { IsCharging = true, Name = "DualSense Edge" };
        viewModel.RefreshStatus();
        Assert.True(viewModel.IsControllerConnected);
        Assert.True(viewModel.IsUsbConnected);
        Assert.False(viewModel.IsBluetoothConnected);
        Assert.Equal("DualSense Edge", viewModel.ControllerName);
        Assert.Equal("—", viewModel.BatteryText);
        Assert.True(viewModel.IsControllerCharging);
        Assert.False(viewModel.CanDisconnectController);
        Assert.Equal(0, engine.StartCount);
    }

    [Theory]
    [InlineData(ControllerTransport.None)]
    [InlineData(ControllerTransport.Usb)]
    public async Task DisconnectIgnoresUnavailableControllers(ControllerTransport transport)
    {
        var controller = new FakeControllerService
        {
            Snapshot = transport == ControllerTransport.None ? ControllerSnapshot.Disconnected : Connected(transport, 80),
        };
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(engine: engine, controller: controller);

        Assert.False(viewModel.CanDisconnectController);
        await viewModel.DisconnectControllerCommand.ExecuteAsync(null);
        Assert.Equal(0, controller.DisconnectCount);
        Assert.Equal(0, engine.StopCount);
    }

    [Fact]
    public async Task DisconnectSuspendsOnlyOutputBeforeTargetingTheDisplayedController()
    {
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations };
        var controller = new FakeControllerService { Snapshot = Connected(ControllerTransport.Bluetooth, null), Operations = operations };
        using var viewModel = CreateViewModel(engine: engine, controller: controller);
        await viewModel.InitializeAsync();
        operations.Clear();

        await viewModel.DisconnectControllerCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "suspend", "disconnect:controller-1", "resume" }, operations);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(0, engine.StopCount);
        Assert.False(viewModel.IsDisconnecting);
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(1, controller.DisconnectCount);
    }

    [Fact]
    public async Task PendingDisconnectBlocksRepeatedDisconnectAndEveryEngineStartPath()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeControllerService
        {
            Snapshot = Connected(ControllerTransport.Bluetooth, 50),
            DisconnectCompletion = completion.Task,
        };
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(engine: engine, controller: controller);
        var disconnect = viewModel.DisconnectControllerCommand.ExecuteAsync(null);
        try
        {
            Assert.True(viewModel.IsDisconnecting);
            Assert.False(viewModel.CanDisconnectController);
            await viewModel.DisconnectControllerCommand.ExecuteAsync(null);
            await viewModel.StartStopCommand.ExecuteAsync(null);
            await viewModel.RestartOutputCommand.ExecuteAsync(null);
            await viewModel.ApplyModesCommand.ExecuteAsync(null);
            await viewModel.InitializeAsync();
            Assert.Equal(1, controller.DisconnectCount);
            Assert.Equal(0, engine.StartCount);
        }
        finally
        {
            completion.TrySetResult();
            await disconnect;
        }
        Assert.False(viewModel.IsDisconnecting);
        Assert.True(viewModel.ControlsEnabled);
    }

    [Fact]
    public async Task DisconnectFailurePreservesActualConnectionAndReportsTheError()
    {
        var controller = new FakeControllerService
        {
            Snapshot = Connected(ControllerTransport.Bluetooth, 40),
            DisconnectException = new IOException("Radio unavailable"),
        };
        var dialogs = new FakeDialogService();
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations };
        using var viewModel = CreateViewModel(engine: engine, dialogs: dialogs, controller: controller);
        await viewModel.InitializeAsync();

        await viewModel.DisconnectControllerCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "start", "suspend", "resume" }, operations);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(0, engine.StopCount);

        Assert.True(viewModel.IsControllerConnected);
        Assert.Equal(40, viewModel.BatteryPercent);
        Assert.False(viewModel.IsDisconnecting);
        Assert.True(viewModel.CanDisconnectController);
        Assert.Contains("Radio unavailable", viewModel.ControllerError);
        Assert.Contains(dialogs.Errors, message => message.Contains("Radio unavailable", StringComparison.Ordinal));
        Assert.Contains(viewModel.LogEntries, entry => entry.Text.Contains("Radio unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposeClosesControllerServiceOnlyOnce()
    {
        var controller = new FakeControllerService();
        var viewModel = CreateViewModel(controller: controller);
        viewModel.Dispose();
        viewModel.Dispose();
        Assert.Equal(1, controller.DisposeCount);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ExportControllerPanelVisualReviewWhenRequested()
    {
        string? output = Environment.GetEnvironmentVariable("FORZAHAPTICS_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(output))
            return;
        Directory.CreateDirectory(output);
        var states = new[]
        {
            ("bluetooth-65", Connected(ControllerTransport.Bluetooth, 65)),
            ("usb-charging-20", Connected(ControllerTransport.Usb, 20) with { IsCharging = true }),
            ("disconnected", ControllerSnapshot.Disconnected),
            ("listening-no-controller", ControllerSnapshot.Disconnected),
        };
        foreach (var (name, state) in states)
        foreach (int width in new[] { 760, 1120 })
        foreach (double scale in new[] { 1d, 1.5d, 2d })
        {
            var engine = new FakeEngineFacade();
            var profiles = new FakeProfileSession();
            if (name == "listening-no-controller")
            {
                engine.StartAsync(new EngineOptions()).GetAwaiter().GetResult();
                profiles.AutoStartListening = false;
                engine.OutputDescription = string.Empty;
            }
            using var viewModel = CreateViewModel(profiles, engine, controller: new FakeControllerService { Snapshot = state });
            var window = new ForzaHaptics.Gui.MainWindow { Width = width, Height = 820, DataContext = viewModel };
            window.Show();
            try
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var content = Assert.IsAssignableFrom<Avalonia.Controls.Control>(window.Content);
                var pixels = new Avalonia.PixelSize((int)Math.Ceiling(content.Bounds.Width * scale),
                    (int)Math.Ceiling(content.Bounds.Height * scale));
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixels, new Avalonia.Vector(96 * scale, 96 * scale));
                bitmap.Render(content);
                string file = Path.Combine(output, $"controller-{name}-{width}-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}x.png");
                bitmap.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Assert.True(new FileInfo(file).Length > 1000, "Expected rendered screenshot pixel data.");
            }
            finally
            {
                window.Close();
            }
        }
    }
    [Fact]
    public async Task DisabledAutoStartLeavesSessionStoppedAndManualStartStillWorks()
    {
        var profiles = new FakeProfileSession { AutoStartListening = false };
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(profiles, engine);
        Assert.False(viewModel.AutoStartListening);
        await viewModel.InitializeAsync();
        Assert.Equal(0, engine.StartCount);
        Assert.True(viewModel.IsEngineStopped);
        await viewModel.StartStopCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(1, engine.StartCount);
        Assert.False(profiles.AutoStartListening);
    }

    [Fact]
    public async Task AutoStartChangesPersistWithoutChangingCurrentSessionOrProfile()
    {
        var profiles = new FakeProfileSession();
        profiles.SwitchTo("Default");
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(profiles, engine);
        Assert.True(viewModel.AutoStartListening);
        await viewModel.InitializeAsync();
        viewModel.AutoStartListening = false;
        Assert.False(profiles.AutoStartListening);
        Assert.True(viewModel.IsEngineRunning);
        Assert.Equal(0, engine.StopCount);
        Assert.False(viewModel.IsDirty);
        viewModel.SelectedProfile = "profile_1";
        Assert.False(viewModel.AutoStartListening);
        await viewModel.StartStopCommand.ExecuteAsync(null);
        viewModel.AutoStartListening = true;
        Assert.True(profiles.AutoStartListening);
        Assert.True(viewModel.IsEngineStopped);
        Assert.Equal(1, engine.StartCount);
        Assert.False(viewModel.IsDirty);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void AutoStartCheckboxSupportsPointerAndKeyboardWithoutExecutingStartStop()
    {
        var profiles = new FakeProfileSession();
        profiles.SwitchTo("Default");
        var engine = new FakeEngineFacade();
        using var viewModel = CreateViewModel(profiles, engine);
        var window = new ForzaHaptics.Gui.MainWindow { DataContext = viewModel, Width = 760, Height = 560 };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var checkbox = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "AutoStartCheckBox");
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), c => c.Name == "StartStopButton");
            Assert.True(checkbox.IsChecked);
            Assert.True(checkbox.IsEnabled);
            Assert.DoesNotContain(button, checkbox.GetVisualAncestors());
            var point = checkbox.TranslatePoint(new Avalonia.Point(10, checkbox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.False(viewModel.AutoStartListening);
            Assert.False(profiles.AutoStartListening);
            checkbox.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.True(viewModel.AutoStartListening);
            Assert.True(profiles.AutoStartListening);
            Assert.Equal(0, engine.StartCount);
            Assert.Equal(0, engine.StopCount);
            Assert.False(viewModel.IsDirty);
        }
        finally { window.Close(); }
    }
    private static ControllerSnapshot Connected(ControllerTransport transport, int? battery) => new()
    {
        DeviceId = "controller-1",
        Name = "DualSense",
        IsConnected = true,
        Transport = transport,
        BatteryPercent = battery,
        CanDisconnect = transport == ControllerTransport.Bluetooth,
    };
    [Fact]
    public async Task XboxAutostartIsIndependentAndRecoveryPrecedesEnabling()
    {
        var profiles = new FakeProfileSession { AutoStartListening = false, AutoStartXboxEmulation = true };
        var engine = new FakeEngineFacade();
        var xbox = new FakeXboxEmulationService();
        using var vm = CreateViewModel(profiles, engine, emulation: xbox);
        await vm.InitializeAsync();
        Assert.Equal(new[] { "recover", "xbox:True" }, xbox.Operations);
        Assert.True(vm.IsXboxEmulationEnabled);
        Assert.Equal(0, engine.StartCount);
        vm.AutoStartXboxEmulation = false;
        vm.SelectedProfile = "Default";
        Assert.False(profiles.AutoStartXboxEmulation);
        Assert.True(xbox.IsEnabled);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task HapticsSuppressesXboxBeforeEveryStartAndReleasesOnlyAfterStop()
    {
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations };
        var xbox = new FakeXboxEmulationService { Operations = operations };
        using var vm = CreateViewModel(engine: engine, emulation: xbox);
        await vm.InitializeAsync();
        await vm.RestartOutputCommand.ExecuteAsync(null);
        vm.IsTestEnabled = true;
        await vm.ApplyModesCommand.ExecuteAsync(null);
        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "recover", "haptics:True", "start", "haptics:True", "start",
            "haptics:True", "start", "stop", "haptics:False" }, operations);
        Assert.False(xbox.IsEnabled);
    }

    [Fact]
    public async Task XboxToggleDoesNotStartOrStopTelemetry()
    {
        var engine = new FakeEngineFacade();
        var xbox = new FakeXboxEmulationService();
        using var vm = CreateViewModel(engine: engine, emulation: xbox);
        await vm.InitializeAsync();
        vm.IsXboxEmulationEnabled = true;
        Assert.True(xbox.IsEnabled);
        vm.IsXboxEmulationEnabled = false;
        Assert.False(xbox.IsEnabled);
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(0, engine.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedEngineStartRestoresXboxRumbleOwnership(bool throws)
    {
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations, StartSucceeds = false, ThrowOnStart = throws };
        var xbox = new FakeXboxEmulationService { Operations = operations };
        using var vm = CreateViewModel(engine: engine, emulation: xbox);
        await vm.InitializeAsync();
        Assert.Equal(new[] { "recover", "haptics:True", "start", "haptics:False" }, operations);
        Assert.False(vm.IsEngineRunning);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedRestartFollowsActualEngineOwnership(bool throws, bool remainsRunning)
    {
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations };
        var xbox = new FakeXboxEmulationService { Operations = operations };
        using var vm = CreateViewModel(engine: engine, emulation: xbox);
        await vm.InitializeAsync();
        Assert.True(vm.IsEngineRunning);
        operations.Clear();
        engine.StartSucceeds = false;
        engine.ThrowOnStart = throws;
        engine.PreserveRunningOnFailedStart = remainsRunning;

        await vm.RestartOutputCommand.ExecuteAsync(null);

        Assert.Equal(remainsRunning, vm.IsEngineRunning);
        Assert.False(vm.IsBusy);
        Assert.Equal(remainsRunning
            ? new[] { "haptics:True", "start" }
            : new[] { "haptics:True", "start", "haptics:False" }, operations);
        if (remainsRunning)
        {
            operations.Clear();
            await vm.StartStopCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "stop", "haptics:False" }, operations);
            Assert.False(vm.IsEngineRunning);
        }
    }

    [Fact]
    public void ShutdownContinuesAfterRumbleResetFailsAndDisposesEngineBeforeXbox()
    {
        var operations = new List<string>();
        var engine = new FakeEngineFacade { Operations = operations };
        var xbox = new FakeXboxEmulationService { Operations = operations, ThrowOnPriority = true };
        var controller = new FakeControllerService();
        var vm = CreateViewModel(engine: engine, controller: controller, emulation: xbox);
        vm.Dispose();
        vm.Dispose();
        Assert.Equal(new[] { "haptics:True", "engine:dispose", "xbox:dispose" }, operations);
        Assert.Equal(1, controller.DisposeCount);
    }

    [Fact]
    public async Task ClosingDuringRecoveryDoesNotAutostartDisposedServices()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new FakeProfileSession { AutoStartListening = true, AutoStartXboxEmulation = true };
        var engine = new FakeEngineFacade();
        var xbox = new FakeXboxEmulationService { InitializeCompletion = completion.Task };
        var vm = CreateViewModel(profiles, engine, emulation: xbox);
        var initialize = vm.InitializeAsync();
        vm.Dispose();
        completion.SetResult();
        await initialize;
        await vm.StartStopCommand.ExecuteAsync(null);
        await vm.InitializeAsync();
        Assert.Equal(0, engine.StartCount);
        Assert.DoesNotContain("xbox:True", xbox.Operations);
    }

    private static MainWindowViewModel CreateViewModel(
        FakeProfileSession? profiles = null,
        FakeEngineFacade? engine = null,
        FakeDialogService? dialogs = null,
        FakeControllerService? controller = null,
        IXboxEmulationService? emulation = null) => new(
            profiles ?? new FakeProfileSession(),
            engine ?? new FakeEngineFacade(),
            dialogs ?? new FakeDialogService(),
            new FakeShellService(),
            new ImmediateDispatcher(),
            controller: controller, emulation: emulation);

    private static SettingFieldViewModel Field(SettingsViewModel settings, string groupTitle, string propertyName) =>
        settings.Tabs.SelectMany(tab => tab.Groups)
            .Single(group => group.Title == groupTitle)
            .Fields.Single(item => item.PropertyName == propertyName);

    private sealed class FakeProfileSession : IProfileSession
    {
        private AppConfig _saved = new();

        public IReadOnlyList<string> Profiles { get; private set; } = new[] { "Default", "profile_1" };
        public string ActiveProfile { get; private set; } = "profile_1";
        public bool IsReadOnly => ActiveProfile == "Default";
        public bool AutoStartListening { get; set; } = true;
        public bool AutoStartXboxEmulation { get; set; }
        public XboxBackend XboxBackend { get; set; }
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
        public string? UnloadableProfile { get; set; }
        public void SwitchTo(string profileName)
        {
            if (profileName == UnloadableProfile)
                throw new InvalidDataException("Invalid profile JSON");
            ActiveProfile = profileName;
        }
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
        public string OutputDescription { get; set; } = "Fake output";
        public string TriggersDescription => string.Empty;
        public bool HasTriggers => false;
        public string TriggerState => string.Empty;
        public float PeakLeft => string.IsNullOrEmpty(OutputDescription) ? 0 : 0.25f;
        public float PeakRight => string.IsNullOrEmpty(OutputDescription) ? 0 : 0.5f;
        public string ActiveEffects => string.Empty;
        public bool StartSucceeds { get; set; } = true;
        public bool ThrowOnStart { get; set; }
        public bool PreserveRunningOnFailedStart { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public EngineOptions? LastOptions { get; private set; }
        public List<string>? Operations { get; init; }

        public Task<bool> StartAsync(EngineOptions options, CancellationToken cancellationToken = default)
        {
            Operations?.Add("start");
            StartCount++;
            LastOptions = options;
            IsRunning = StartSucceeds || (PreserveRunningOnFailedStart && IsRunning);
            return ThrowOnStart
                ? Task.FromException<bool>(new IOException("Output startup failed"))
                : Task.FromResult(StartSucceeds);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Operations?.Add("stop");
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task SuspendOutputAsync(CancellationToken cancellationToken = default)
        {
            Operations?.Add("suspend");
            return Task.CompletedTask;
        }

        public Task ResumeOutputAsync(CancellationToken cancellationToken = default)
        {
            Operations?.Add("resume");
            return Task.CompletedTask;
        }

        public string BuildStatus() => IsRunning ? "Running" : "Stopped";
        public void Dispose() => Operations?.Add("engine:dispose");
    }

    private sealed class FakeXboxEmulationService : IXboxEmulationService
    {
        public bool IsEnabled { get; private set; }
        public string? StatusOverride { get; set; }
        public string Status => StatusOverride ?? (IsEnabled ? "Connected" : "Disabled");
        public string? Error => null;
        public XboxEmulationState State { get; set; }
        public bool IsBusy => State is XboxEmulationState.Starting or XboxEmulationState.Stopping;
        public List<string> Operations { get; init; } = new();
        public Task InitializeCompletion { get; init; } = Task.CompletedTask;
        public Task InitializeAsync() { Operations.Add("recover"); return InitializeCompletion; }
        public Task SetEnabledAsync(bool value) { if (value) BackendAtEnable = Backend; IsEnabled = value; Operations.Add($"xbox:{value}"); return Task.CompletedTask; }
        public XboxBackend Backend { get; private set; }
        public XboxBackend BackendAtEnable { get; private set; }
        public int InstallCount { get; private set; }
        public Task SetBackendAsync(XboxBackend backend) { Backend = backend; return Task.CompletedTask; }
        public Task InstallHidMaestroAsync() { InstallCount++; return Task.CompletedTask; }
        public Task CheckAgainAsync() { Operations.Add("xbox:retry"); return Task.CompletedTask; }
        public bool ThrowOnPriority { get; set; }
        public void SetHapticsActive(bool value)
        {
            Operations.Add($"haptics:{value}");
            if (ThrowOnPriority) throw new IOException("Controller disappeared");
        }
        public Task SuspendAsync() { Operations.Add("xbox:suspend"); return Task.CompletedTask; }
        public void Resume() => Operations.Add("xbox:resume");
        public void Dispose() => Operations.Add("xbox:dispose");
    }

    private sealed class FakeDialogService : IUserDialogService
    {
        public List<string> Errors { get; } = new();
        public bool ConfirmInstall { get; set; }
        public Task<bool> ConfirmHidMaestroInstallationAsync(CancellationToken cancellationToken = default) => Task.FromResult(ConfirmInstall);
        public UnsavedChangesDecision UnsavedDecision { get; set; } = UnsavedChangesDecision.Save;

        public Task<string?> RequestTextAsync(string title, string prompt, string initialValue, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(initialValue);
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string profileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnsavedDecision);
        public Task<bool> ConfirmProfileDeletionAsync(string profileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task ShowWarningAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowErrorAsync(string message, CancellationToken cancellationToken = default)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeControllerService : IControllerService
    {
        public ControllerSnapshot Snapshot { get; set; } = ControllerSnapshot.Disconnected;
        public int DisconnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public List<string>? Operations { get; init; }
        public Task DisconnectCompletion { get; init; } = Task.CompletedTask;
        public Exception? DisconnectException { get; init; }

        public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            Operations?.Add($"disconnect:{deviceId}");
            if (DisconnectException is not null)
                throw DisconnectException;
            await DisconnectCompletion;
        }

        public void Dispose() => DisposeCount++;
    }
    private sealed class FakeShellService : IPlatformShellService
    {
        public void OpenDirectory(string path) { }
        public void OpenUrl(string url) { }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
