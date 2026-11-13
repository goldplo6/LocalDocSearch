using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (settingsPath, maxConcurrency) = ParseStartupArguments(desktop.Args ?? []);
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(new LocalDocSearch.Settings.JsonSettingsStore(settingsPath), maxConcurrency: maxConcurrency) };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static (string? SettingsPath, int MaxConcurrency) ParseStartupArguments(string[] args)
    {
        if (args.Length == 0) return (null, 1);
        if (args.Length == 2 && args[0] == "--settings" && !string.IsNullOrWhiteSpace(args[1]))
            return (args[1], 1);
        if (args.Length == 4 && args[0] == "--settings" && !string.IsNullOrWhiteSpace(args[1]) &&
            args[2] == "--experiment-workers" && args[3] is "1" or "2" or "4")
            return (args[1], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
        throw new ArgumentException("启动参数无效。支持无参数、--settings PATH，或 --settings PATH --experiment-workers N；N 仅支持 1、2 或 4。");
    }
}
