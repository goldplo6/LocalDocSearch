using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private TextEncodingMode _resultEncodingMode;
    private DocumentScopeOptions _resultScope = new();
    public event Action<MatchPreviewRequest>? PreviewRequested;

    [RelayCommand]
    private void PreviewFile(FileResultViewModel? file)
    {
        if (file?.FirstMatch is { } match) RequestPreview(file, match);
    }

    [RelayCommand]
    private void PreviewMatch(MatchViewModel? match)
    {
        if (match?.FullPath is null) return;
        var file = Results.FirstOrDefault(f => string.Equals(f.FullPath, match.FullPath, StringComparison.OrdinalIgnoreCase));
        if (file is not null) RequestPreview(file, match.SourceMatch);
    }

    private void RequestPreview(FileResultViewModel file, MatchItem match)
    {
        if (PreviewRequested is null) { StatusText = "文字预览窗口尚未连接。"; return; }
        PreviewRequested(new MatchPreviewRequest(file.FullPath, match, file.LastWriteTimeUtc, _resultEncodingMode, _resultScope));
        StatusText = "已打开命中文字预览；文字选区核验结果见预览窗口。";
    }
}
