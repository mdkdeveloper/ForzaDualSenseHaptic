using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ForzaHaptics.Gui;

[assembly: AvaloniaTestApplication(typeof(ForzaHaptics.Tests.TestAppBuilder))]

namespace ForzaHaptics.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
