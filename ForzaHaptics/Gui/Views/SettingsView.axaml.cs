using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ForzaHaptics.Gui.ViewModels;

namespace ForzaHaptics.Gui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private static void Commit(TextBox textBox)
    {
        switch (textBox.DataContext)
        {
            case SliderSettingFieldViewModel slider:
                slider.TryCommitText();
                break;
            case TextSettingFieldViewModel text:
                text.TryCommitText();
                break;
            case ListSettingFieldViewModel list:
                list.TryCommitText();
                break;
        }
    }

    private void OnCommitText(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is TextBox textBox)
            Commit(textBox);
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || sender is not TextBox textBox)
            return;
        Commit(textBox);
        textBox.SelectAll();
        eventArgs.Handled = true;
    }
}
