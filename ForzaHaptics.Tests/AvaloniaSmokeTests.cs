using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ForzaHaptics.Gui;
using ForzaHaptics.Gui.Dialogs;
using ForzaHaptics.Gui.ViewModels;
using ForzaHaptics.Gui.Views;

namespace ForzaHaptics.Tests;

public sealed class AvaloniaSmokeTests
{
    [AvaloniaFact]
    public async Task ClosingAdministratorRestartDialogCancelsInsteadOfAccepting()
    {
        var owner = new Window();
        var dialog = new MessageDialog("Administrator permissions", "Restart to enable Impulse Triggers?",
            "Restart as administrator", cancelText: "Cancel");
        owner.Show();
        try
        {
            Task<MessageDialogResult> result = dialog.ShowDialog<MessageDialogResult>(owner);
            dialog.Close();
            Assert.Equal(MessageDialogResult.Cancel, await result);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void ApplicationUsesDarkThemeAndReadableSemanticPalette()
    {
        var application = Assert.IsType<App>(Application.Current);

        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

        AssertBrushColor("AppBackgroundBrush", "#0F1117");
        AssertBrushColor("AppSurfaceBrush", "#171B23");
        AssertBrushColor("AppCardBrush", "#1E2430");
        AssertBrushColor("AppInputBrush", "#222936");
        AssertBrushColor("AppBorderBrush", "#343D4D");
        AssertBrushColor("AppControlBorderBrush", "#687386");
        AssertBrushColor("AppTextPrimaryBrush", "#F4F7FB");
        AssertBrushColor("AppTextSecondaryBrush", "#AAB4C3");
        AssertBrushColor("AppAccentBrush", "#4C8DFF");
        AssertBrushColor("AppFocusBrush", "#80B0FF");
        AssertBrushColor("AppErrorSurfaceBrush", "#3A2028");
        AssertBrushColor("AppErrorBrush", "#FF6B7A");
        AssertBrushColor("AppLogBrush", "#0B0E13");
        AssertBrushColor("AppSuccessBrush", "#63D98B");
        AssertBrushColor("AppWarningBrush", "#FFBE6A");

        AssertContrast("AppTextPrimaryBrush", "AppBackgroundBrush", 4.5);
        AssertContrast("AppTextPrimaryBrush", "AppCardBrush", 4.5);
        AssertContrast("AppTextSecondaryBrush", "AppBackgroundBrush", 4.5);
        AssertContrast("AppTextSecondaryBrush", "AppSurfaceBrush", 4.5);
        AssertContrast("AppAccentBrush", "AppBackgroundBrush", 4.5);
        AssertContrast("AppErrorBrush", "AppErrorSurfaceBrush", 4.5);
        AssertContrast("AppSuccessBrush", "AppLogBrush", 4.5);
        AssertContrast("AppWarningBrush", "AppLogBrush", 4.5);
        AssertContrast("AppControlBorderBrush", "AppInputBrush", 3.0);
    }

    [AvaloniaFact]
    public void DarkTopLevelViewsAndDialogsLoad()
    {
        var mainWindow = new MainWindow();
        var inputDialog = new InputDialog();
        var messageDialog = new MessageDialog();
        var settingsView = new SettingsView
        {
            DataContext = new SettingsViewModel(new AppConfig()),
        };
        var host = new Window { Content = settingsView };

        mainWindow.Show();
        inputDialog.Show();
        messageDialog.Show();
        host.Show();

        try
        {
            Assert.Equal(GetBrush("AppBackgroundBrush").Color, Assert.IsType<SolidColorBrush>(mainWindow.Background).Color);
            Assert.Equal(GetBrush("AppSurfaceBrush").Color, Assert.IsType<SolidColorBrush>(inputDialog.Background).Color);
            Assert.Equal(GetBrush("AppSurfaceBrush").Color, Assert.IsType<SolidColorBrush>(messageDialog.Background).Color);

            var activityLog = Assert.Single(mainWindow.GetVisualDescendants().OfType<Expander>());
            Assert.False(activityLog.IsExpanded);

            var tabs = Assert.Single(settingsView.GetVisualDescendants().OfType<TabControl>());
            Assert.Equal(Dock.Left, tabs.TabStripPlacement);

            var selectedTab = Assert.Single(settingsView.GetVisualDescendants().OfType<TabItem>(), tab => tab.IsSelected);
            Assert.Equal(GetBrush("AppSelectedBrush").Color, Assert.IsType<SolidColorBrush>(selectedTab.Background).Color);

            var slider = Assert.IsAssignableFrom<Slider>(settingsView.GetVisualDescendants().OfType<Slider>().First());
            Assert.Equal(GetBrush("AppAccentBrush").Color, Assert.IsType<SolidColorBrush>(slider.Foreground).Color);
        }
        finally
        {
            host.Close();
            messageDialog.Close();
            inputDialog.Close();
            mainWindow.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsViewRendersAllThreeTabsAndGeneratedGroups()
    {
        var settings = new SettingsViewModel(new AppConfig());
        var view = new SettingsView { DataContext = settings };
        var host = new Window { Content = view };

        host.Show();

        try
        {
            TabControl tabs = Assert.Single(view.GetVisualDescendants().OfType<TabControl>());
            Assert.Equal(3, tabs.ItemCount);
            Assert.Equal(Dock.Left, tabs.TabStripPlacement);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), textBlock => textBlock.Text == "Connection");
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void PrimaryControlsAndSettingsCardsStayWithinSupportedWindowWidths()
    {
        foreach ((double width, double height) in new[]
                 {
                     (760d, 560d),
                     (1040d, 820d),
                     (1440d, 900d),
                 })
        {
            var mainWindow = new MainWindow { Width = width, Height = height };
            mainWindow.Show();

            try
            {
                var controllerPanel = NamedControl<Control>(mainWindow, "ControllerPanel");
                var battery = NamedControl<Control>(mainWindow, "ControllerBattery");
                var disconnect = NamedControl<Button>(mainWindow, "DisconnectControllerButton");
                var profileSelector = NamedControl<ComboBox>(mainWindow, "ProfileSelector");
                var impulseTriggers = NamedControl<CheckBox>(mainWindow, "ImpulseTriggersCheckBox");
                Assert.Equal("Impulse Triggers", impulseTriggers.Content);
                AssertHorizontallyInside(mainWindow, impulseTriggers);
                AssertHorizontallyInside(mainWindow, controllerPanel);
                AssertHorizontallyInside(mainWindow, battery);
                AssertHorizontallyInside(mainWindow, disconnect);
                AssertHorizontallyInside(mainWindow, profileSelector);
                var panelOrigin = controllerPanel.TranslatePoint(default, mainWindow)!.Value;
                var profileOrigin = profileSelector.TranslatePoint(default, mainWindow)!.Value;
                Assert.True(panelOrigin.Y >= 0);
                Assert.True(panelOrigin.Y + controllerPanel.Bounds.Height <= profileOrigin.Y + 0.5,
                    "Controller status must remain above the profile controls.");
                Assert.True(battery.Bounds.Height > 0);
                Assert.True(disconnect.Bounds.Height > 0);
                AssertHorizontallyInside(mainWindow, NamedControl<Button>(mainWindow, "RevertButton"));
                AssertHorizontallyInside(mainWindow, NamedControl<Button>(mainWindow, "SaveButton"));
                AssertHorizontallyInside(mainWindow, NamedControl<Button>(mainWindow, "StartStopButton"));
                AssertHorizontallyInside(mainWindow, NamedControl<Expander>(mainWindow, "ActivityLogExpander"));
            }
            finally
            {
                mainWindow.Close();
            }

            var settingsView = new SettingsView
            {
                DataContext = new SettingsViewModel(new AppConfig()),
            };
            var host = new Window { Width = width, Height = height, Content = settingsView };
            host.Show();

            try
            {
                AssertHorizontallyInside(host, Assert.Single(settingsView.GetVisualDescendants().OfType<TabControl>()));
                foreach (var card in settingsView.GetVisualDescendants().OfType<GroupBox>())
                    AssertHorizontallyInside(host, card);
            }
            finally
            {
                host.Close();
            }
        }
    }

    private static void AssertBrushColor(string resourceKey, string expected)
    {
        Assert.Equal(Color.Parse(expected), GetBrush(resourceKey).Color);
    }

    private static void AssertContrast(string foregroundKey, string backgroundKey, double minimumRatio)
    {
        var foreground = GetBrush(foregroundKey).Color;
        var background = GetBrush(backgroundKey).Color;
        var ratio = ContrastRatio(foreground, background);

        Assert.True(
            ratio >= minimumRatio,
            $"Expected {foregroundKey} on {backgroundKey} to have a contrast ratio of at least " +
            $"{minimumRatio:F1}:1, but it was {ratio:F2}:1.");
    }

    private static SolidColorBrush GetBrush(string resourceKey)
    {
        var application = Assert.IsType<App>(Application.Current);
        Assert.True(
            application.TryGetResource(resourceKey, ThemeVariant.Dark, out var resource),
            $"Application resource '{resourceKey}' was not found for the dark theme.");
        return Assert.IsType<SolidColorBrush>(resource);
    }

    private static T NamedControl<T>(Control root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static void AssertHorizontallyInside(Window window, Control control)
    {
        var origin = control.TranslatePoint(default, window);
        Assert.True(origin.HasValue, $"Could not transform {control.Name ?? control.GetType().Name} to the window.");
        Assert.True(control.Bounds.Width > 0, $"{control.Name ?? control.GetType().Name} has no width.");
        Assert.InRange(origin.Value.X, -0.5, window.ClientSize.Width);
        Assert.InRange(origin.Value.X + control.Bounds.Width, 0, window.ClientSize.Width + 0.5);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R) +
        0.7152 * Linearize(color.G) +
        0.0722 * Linearize(color.B);

    private static double Linearize(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
