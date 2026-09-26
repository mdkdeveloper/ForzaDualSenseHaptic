using Avalonia.Controls;
using Avalonia.Threading;
using ForzaHaptics.Gui.Dialogs;

namespace ForzaHaptics.Gui.Services;

public sealed class AvaloniaUserDialogService : IUserDialogService
{
    private readonly Func<Window?> _owner;

    public AvaloniaUserDialogService(Func<Window?> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public Task<string?> RequestTextAsync(
        string title,
        string prompt,
        string initialValue,
        CancellationToken cancellationToken = default) =>
        ShowAsync<string>(new InputDialog(title, prompt, initialValue), cancellationToken);

    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        var dialog = new MessageDialog(
            "ForzaHaptics",
            $"Save changes to profile '{profileName}'?",
            "Save",
            "Don't save",
            "Cancel");
        return await ShowAsync<MessageDialogResult>(dialog, cancellationToken) switch
        {
            MessageDialogResult.Primary => UnsavedChangesDecision.Save,
            MessageDialogResult.Secondary => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel,
        };
    }

    public async Task<bool> ConfirmProfileDeletionAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        var dialog = new MessageDialog(
            "ForzaHaptics",
            $"Delete profile '{profileName}'? The file will be moved to the Recycle Bin.",
            "Delete",
            cancelText: "Cancel");
        return await ShowAsync<MessageDialogResult>(dialog, cancellationToken) == MessageDialogResult.Primary;
    }

    public async Task ShowWarningAsync(string message, CancellationToken cancellationToken = default) =>
        await ShowAsync<MessageDialogResult>(new MessageDialog("ForzaHaptics — Warning", message, "OK"), cancellationToken);

    public async Task ShowErrorAsync(string message, CancellationToken cancellationToken = default) =>
        await ShowAsync<MessageDialogResult>(new MessageDialog("ForzaHaptics — Error", message, "OK"), cancellationToken);

    private async Task<T?> ShowAsync<T>(Window dialog, CancellationToken cancellationToken)
    {
        Window owner = _owner() ?? throw new InvalidOperationException("The main window is not available.");
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(() => dialog.Close(default(T))));
        return await dialog.ShowDialog<T?>(owner);
    }
}
