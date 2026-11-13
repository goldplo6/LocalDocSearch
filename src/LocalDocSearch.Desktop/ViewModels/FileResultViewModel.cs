using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class FileResultViewModel : ObservableObject
{
    private const int PageSize = 50;
    private readonly FileResult _file;
    // Resolve presentation metadata on demand, then reuse it for repeated row bindings.
    private string? _fileName, _matchCountText, _modifiedText, _extensionLabel, _title;

    [ObservableProperty]
    private bool _isExpanded;

    public FileResultViewModel(FileResult file)
    {
        _file = file;
        LoadMore();
    }

    public string FullPath => _file.FullPath;
    public string FileName => _fileName ??= Path.GetFileName(FullPath);
    public string MatchCountText => _matchCountText ??= $"{_file.TotalOccurrences:N0} 处匹配";
    public string ModifiedText => _modifiedText ??= $"{_file.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    public string ExtensionLabel => _extensionLabel ??= Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
    public string FileTypeLabel => ExtensionLabel.Length == 0 ? "FILE" : ExtensionLabel;
    public bool IsPdfFile => ExtensionLabel == "PDF";
    public bool IsWordFile => ExtensionLabel == "DOCX";
    public bool IsSpreadsheetFile => ExtensionLabel is "XLSX" or "CSV";
    public bool IsPresentationFile => ExtensionLabel == "PPTX";
    public bool IsMarkdownFile => ExtensionLabel == "MD";
    public string Title => _title ??= $"{FileName}  ·  {_file.TotalOccurrences:N0} 处  ·  {ModifiedText}";
    public int TotalOccurrences => _file.TotalOccurrences;
    public DateTime LastWriteTimeUtc => _file.LastWriteTimeUtc;
    public MatchItem? FirstMatch => _file.Matches.FirstOrDefault();
    public string FirstBefore => FirstMatch?.Before ?? string.Empty;
    public string FirstText => FirstMatch?.Text ?? string.Empty;
    public string FirstAfter => FirstMatch?.After ?? string.Empty;
    public bool HasWarnings => _file.ExtractionWarnings.Count > 0;
    public string WarningsText => string.Join(Environment.NewLine, _file.ExtractionWarnings);
    public ObservableCollection<MatchViewModel> VisibleMatches { get; } = [];
    public bool HasMore => VisibleMatches.Count < _file.Matches.Count;
    public string LoadedText => $"已展示 {VisibleMatches.Count:N0} / {_file.Matches.Count:N0} 处";

    [RelayCommand]
    private void LoadMore()
    {
        var end = Math.Min(VisibleMatches.Count + PageSize, _file.Matches.Count);
        while (VisibleMatches.Count < end)
            VisibleMatches.Add(new MatchViewModel(_file.Matches[VisibleMatches.Count], FullPath));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(LoadedText));
    }
}

public sealed class MatchViewModel(MatchItem match, string? fullPath = null)
{
    public MatchItem SourceMatch => match;
    public string? FullPath => fullPath;
    public string Position => $"{SourcePositionFormatter.Format(match.Location)} · 关键词：{match.Term}";
    public string Before => match.Before;
    public string Text => match.Text;
    public string After => match.After;
}
