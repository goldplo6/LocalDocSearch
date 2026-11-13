using Avalonia;
using Avalonia.Headless;
using LocalDocSearch.Desktop;

[assembly: AvaloniaTestApplication(typeof(LocalDocSearch.Desktop.Tests.TestAppBuilder))]

namespace LocalDocSearch.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
