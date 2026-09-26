using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ForzaHaptics.Gui;

[assembly: AvaloniaTestApplication(typeof(ForzaHaptics.Tests.TestAppBuilder))]

namespace ForzaHaptics.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        bool renderScreenshots = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORZAHAPTICS_SCREENSHOT_DIR"));
        var builder = AppBuilder.Configure<App>();
        if (renderScreenshots)
            builder.UseSkia();
        return builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !renderScreenshots });
    }
}
