using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ForzaHaptics.Controllers;
using ForzaHaptics.Gui.Dialogs;
using ForzaHaptics.Gui.Services;
using ForzaHaptics.Gui.ViewModels;
using ForzaHaptics.Util;

namespace ForzaHaptics.Gui;

internal static class GuiApp
{
    public static int Run(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

    internal static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect();
}

public sealed class App : Application
{
    private bool _showingUnhandledException;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        desktop.Exit += (_, _) => Dispatcher.UIThread.UnhandledException -= OnUnhandledException;

        var earlyLog = new List<(LogLevel Level, string Text)>();
        void Collect(LogLevel level, string text) => earlyLog.Add((level, text));
        Log.Message += Collect;

        ConfigManager? config = null;
        try
        {
            Log.Info("ForzaHaptics — Forza Horizon 6 telemetry → DualSense haptics");
            ProfileStore store = ProfileStore.Open();
            config = store.OpenActive(out string profile);
            var session = new ProfileSession(store, config, profile);
            config = null; // Ownership moved to ProfileSession.

            MainWindow? window = null;
            var dialogs = new AvaloniaUserDialogService(() => window);
            ControllerService? controller = null;
            var engine = new HapticEngineFacade(() => session.Current, () => session.Revision,
                () => controller?.Snapshot ?? ControllerSnapshot.Disconnected);
            controller = new ControllerService(
                () => session.Current.Output,
                () => engine.IsRunning ? engine.ActiveControllerDeviceId : null);
            var viewModel = new MainWindowViewModel(
                session,
                engine,
                dialogs,
                new WindowsPlatformShellService(),
                new AvaloniaUiDispatcher(),
                earlyLog,
                controller);
            window = new MainWindow(viewModel);
            desktop.MainWindow = window;
        }
        catch (Exception exception)
        {
            config?.Dispose();
            desktop.MainWindow = new MessageDialog(
                "ForzaHaptics — Error",
                $"Failed to load settings:{Environment.NewLine}{exception.Message}",
                "Close");
        }
        finally
        {
            Log.Message -= Collect;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        Log.Error($"Unhandled error: {eventArgs.Exception.Message}");
        if (_showingUnhandledException ||
            ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            return;

        _showingUnhandledException = true;
        try
        {
            var dialog = new MessageDialog(
                "ForzaHaptics — Error",
                eventArgs.Exception.ToString(),
                "OK");
            await dialog.ShowDialog<MessageDialogResult>(owner);
        }
        catch
        {
            // Avoid recursing through the unhandled-exception handler if the dialog itself fails.
        }
        finally
        {
            _showingUnhandledException = false;
        }
    }

}
