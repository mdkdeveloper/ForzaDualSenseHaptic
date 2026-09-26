using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ForzaHaptics.Gui.Dialogs;

internal enum MessageDialogResult
{
    Primary,
    Secondary,
    Cancel,
}

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    internal MessageDialog(
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null)
        : this()
    {
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
        SecondaryButton.IsVisible = secondaryText is not null;
        CancelButton.Content = cancelText;
        CancelButton.IsVisible = cancelText is not null;
    }

    private void OnPrimary(object? sender, RoutedEventArgs eventArgs) => Complete(MessageDialogResult.Primary);
    private void OnSecondary(object? sender, RoutedEventArgs eventArgs) => Complete(MessageDialogResult.Secondary);
    private void OnCancel(object? sender, RoutedEventArgs eventArgs) => Complete(MessageDialogResult.Cancel);

    private void Complete(MessageDialogResult result)
    {
        if (Owner is null)
            Close();
        else
            Close(result);
    }
}
