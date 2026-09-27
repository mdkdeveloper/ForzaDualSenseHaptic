using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ForzaHaptics.Gui.ViewModels;

namespace ForzaHaptics.Gui;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private MainWindowViewModel? _viewModel;
    private bool _allowClose;
    private bool _closePromptActive;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _statusTimer.Tick += (_, _) => _viewModel?.RefreshStatus();
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;
        viewModel.RestartRequested += OnRestartRequested;
    }

    private void OnRestartRequested(object? sender, EventArgs eventArgs)
    {
        _allowClose = true;
        Close();
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is null)
            return;
        _statusTimer.Start();
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_allowClose || _viewModel is null)
            return;
        eventArgs.Cancel = true;
        if (_closePromptActive)
            return;

        _closePromptActive = true;
        try
        {
            if (!await _viewModel.ConfirmCloseAsync())
                return;
            _allowClose = true;
            Close();
        }
        finally
        {
            _closePromptActive = false;
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.NewItems is { Count: > 0 } && eventArgs.NewItems[^1] is { } item)
            LogList.ScrollIntoView(item);
    }

    private void OnLogExpanded(object? sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel?.LogEntries is not { Count: > 0 } entries)
            return;

        LogEntryViewModel lastEntry = entries[^1];
        Dispatcher.UIThread.Post(
            () => LogList.ScrollIntoView(lastEntry),
            DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _statusTimer.Stop();
        if (_viewModel is null)
            return;
        _viewModel.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _viewModel.RestartRequested -= OnRestartRequested;
        _viewModel.Dispose();
        _viewModel = null;
    }
}
