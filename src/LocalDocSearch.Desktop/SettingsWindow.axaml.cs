using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();
    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) Close(vm.Build());
    }
    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);
}
