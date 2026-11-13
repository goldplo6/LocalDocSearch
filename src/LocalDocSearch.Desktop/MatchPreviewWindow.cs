using Avalonia.Controls;
using LocalDocSearch.Desktop.Controls;

namespace LocalDocSearch.Desktop;

/// <summary>Standalone host for the same verified read-only preview used by the sidebar.</summary>
public sealed class MatchPreviewWindow : Window
{
    private readonly MatchPreviewPane _pane;
    private readonly MatchPreviewRequest _request;
    private readonly MatchPreviewLoader? _loader;
    public bool SelectionVerified => _pane.SelectionVerified;
    public Task SelectionTask => _pane.SelectionTask;

    public MatchPreviewWindow(MatchPreviewRequest request, MatchPreviewLoader? loader = null)
    {
        _request = request;
        _loader = loader;
        Title = $"命中文字预览 · {Path.GetFileName(request.FullPath)}";
        Width = 900; Height = 660; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _pane = new MatchPreviewPane { CanPopOut = false };
        _pane.CloseRequested += (_, _) => Close();
        Content = _pane;
        Opened += (_, _) => _ = LoadSelectionAsync();
        Closed += (_, _) => _pane.Dispose();
    }

    public Task LoadSelectionAsync() => _pane.ShowAsync(_request, _loader);
}
