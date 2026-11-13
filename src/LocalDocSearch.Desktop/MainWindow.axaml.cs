using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _observedViewModel;
    private MatchPreviewWindow? _previewWindow;
    private MatchPreviewRequest? _inlineRequest;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowDensity();
        InlinePreviewPane.CloseRequested += InlinePreview_OnCloseRequested;
        InlinePreviewPane.PopOutRequested += InlinePreview_OnPopOutRequested;
        SizeChanged += (_, _) =>
        {
            UpdateWindowDensity();
            if (ClientSize.Width < 1100 && _inlineRequest is { } request)
            {
                ClearInlinePreview();
                ShowStandalonePreview(request);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_observedViewModel is not null) _observedViewModel.PreviewRequested -= ShowPreview;
            ClearInlinePreview();
            _previewWindow?.Close();
            _observedViewModel = DataContext as MainWindowViewModel;
            if (!_isClosed && _observedViewModel is not null) _observedViewModel.PreviewRequested += ShowPreview;
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _previewWindow?.Close();
            if (_observedViewModel is not null) _observedViewModel.PreviewRequested -= ShowPreview;
            _observedViewModel = null;
            ClearInlinePreview();
            InlinePreviewPane.CloseRequested -= InlinePreview_OnCloseRequested;
            InlinePreviewPane.PopOutRequested -= InlinePreview_OnPopOutRequested;
            InlinePreviewPane.Dispose();
        };
        Closing += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (vm.IsBusy) vm.CancelSearchCommand.Execute(null);
            vm.SaveSettings();
        };
    }

    private void UpdateWindowDensity()
    {
        // Keep the three search rows, formula and result toolbar visible at 640 DIP.
        var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
        Classes.Set("compact", height <= 700);
    }

    private void ShowPreview(MatchPreviewRequest request)
    {
        if (_isClosed) return;
        _previewWindow?.Close();
        ClearInlinePreview();
        if (ClientSize.Width >= 1100)
        {
            _inlineRequest = request;
            InlinePreviewHost.IsVisible = true;
            _ = InlinePreviewPane.ShowAsync(request);
        }
        else ShowStandalonePreview(request);
    }

    private void ShowStandalonePreview(MatchPreviewRequest request)
    {
        if (_isClosed) return;
        _previewWindow?.Close();
        var preview = new MatchPreviewWindow(request);
        _previewWindow = preview;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_previewWindow, preview)) _previewWindow = null;
        };
        preview.Show(this);
    }

    private void ClearInlinePreview()
    {
        _inlineRequest = null;
        InlinePreviewPane.Clear();
        InlinePreviewHost.IsVisible = false;
    }

    private void InlinePreview_OnCloseRequested(object? sender, EventArgs e) => ClearInlinePreview();

    private void InlinePreview_OnPopOutRequested(object? sender, EventArgs e)
    {
        if (_inlineRequest is not { } request) return;
        ClearInlinePreview();
        ShowStandalonePreview(request);
    }

    // Flyout content has its own visual root; resolve commands through this window,
    // using the originating row's data context instead of a popup ancestor binding.
    private void FileAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainWindowViewModel vm) return;
        switch (button.Tag)
        {
            case "FindFile" when button.DataContext is FileResultViewModel file: vm.OpenFileCommand.Execute(file); break;
            case "LocateFile" when button.DataContext is FileResultViewModel file: vm.LocateFileCommand.Execute(file); break;
            case "RevealFile" when button.DataContext is FileResultViewModel file: vm.RevealFileCommand.Execute(file); break;
            case "FindMatch" when button.DataContext is MatchViewModel match: vm.FindOriginalMatchCommand.Execute(match); break;
            case "LocateMatch" when button.DataContext is MatchViewModel match: vm.LocateMatchCommand.Execute(match); break;
        }
        foreach (var owner in this.GetVisualDescendants().OfType<Button>()) owner.Flyout?.Hide();
        e.Handled = true;
    }

    private async void BrowseFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择搜索目录",
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.RootFolder = path;
        }
        catch (Exception ex) { vm.StatusText = $"选择目录失败：{ex.Message}"; }
    }

    private void Keyword_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel vm) return;
        if (sender is TextBox box && box.GetVisualDescendants().OfType<TextPresenter>()
                .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText))) return;
        vm.AddTermCommand.Execute(null);
        e.Handled = true;
    }

    private async void CopyPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FileResultViewModel file } || DataContext is not MainWindowViewModel vm) return;
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(file.FullPath);
                vm.StatusText = "已复制文件路径。";
            }
            else vm.StatusText = "当前环境没有可用剪贴板。";
        }
        catch (Exception ex) { vm.StatusText = $"复制失败：{ex.Message}"; }
    }

    private async void CopyDiagnostics_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { HasDiagnostics: true } vm) return;
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.CreateDiagnosticSummary());
                vm.StatusText = "已复制聚合诊断摘要；未包含目录、文件路径、关键词、正文或错误原文。";
            }
            else vm.StatusText = "当前环境没有可用剪贴板。";
        }
        catch (Exception ex) { vm.StatusText = $"复制诊断失败：{ex.Message}"; }
    }

    private async void CopyOriginalFind_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { HasOriginalFindTerm: true } vm) return;
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.OriginalFindSearchText);
                vm.StatusText = "已复制查找词；请在原程序按 Ctrl+F 后粘贴。此操作未声明精确来源定位。";
            }
            else vm.StatusText = "当前环境没有可用剪贴板。";
        }
        catch (Exception ex) { vm.StatusText = $"复制查找词失败：{ex.Message}"; }
    }

    private async void Grouping_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { IsBusy: false } vm || vm.CreateQueryEditor() is not { } editor) return;
        var dialog = new QueryGroupingWindow(editor);
        var snapshot = await dialog.ShowDialog<GroupedQuerySnapshot?>(this);
        if (snapshot is not null) vm.ApplyGroupedQuery(snapshot);
    }

    private async void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { IsBusy: false } vm) return;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(vm.CaptureSettings()) };
        var settings = await dialog.ShowDialog<LocalDocSearch.Settings.AppSettings?>(this);
        if (settings is not null) vm.ApplySettings(settings, save: true);
    }

    private void Result_OnExpanding(object? sender, CancelRoutedEventArgs e)
    {
        // Restoring an expanded file into a recycled container must not move the viewport.
        if (sender is not Expander { DataContext: FileResultViewModel { IsExpanded: false } file }) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (file.IsExpanded && this.FindControl<ListBox>("ResultsList") is { } results && results.Items.Contains(file))
                results.ScrollIntoView(file);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void Match_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MatchViewModel match } && DataContext is MainWindowViewModel vm)
        {
            vm.PreviewMatchCommand.Execute(match);
            e.Handled = true;
        }
    }

    private void Result_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is MainWindowViewModel vm && this.FindControl<ListBox>("ResultsList")?.SelectedItem is FileResultViewModel file)
            vm.PreviewFileCommand.Execute(file);
    }
}
