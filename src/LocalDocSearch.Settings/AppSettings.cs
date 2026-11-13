using LocalDocSearch.Core;
using LocalDocSearch.Extractors;

namespace LocalDocSearch.Settings;

/// <summary>Local preferences only; search terms, results and extracted text are never persisted.</summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string RootFolder { get; init; } = string.Empty;
    public bool UseAnd { get; init; } = true;
    public bool MatchCase { get; init; }
    public string[] Extensions { get; init; } = [".txt", ".md", ".log", ".csv", ".docx", ".xlsx", ".pptx", ".pdf"];
    public int ContextCharacters { get; init; } = 60;
    public int SortMode { get; init; }
    public TextEncodingMode EncodingMode { get; init; } = TextEncodingMode.StrictUnicode;
    public bool AllowOfficeAutomation { get; init; }
    public DocumentScopeOptions Scope { get; init; } = new();
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);
