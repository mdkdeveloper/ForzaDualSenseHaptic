using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ForzaHaptics.Gui.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    internal InputDialog(string title, string prompt, string initialValue)
        : this()
    {
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
    }

    private void OnAccept(object? sender, RoutedEventArgs eventArgs) => Close(ValueBox.Text);

    private void OnCancel(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private void OnValueKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
            return;
        Close(ValueBox.Text);
        eventArgs.Handled = true;
    }
}
