namespace LocalDocSearch.Core;

public enum SearchOperator { And, Or }
public enum SearchMatchMode { Literal, RegularExpression }
public enum SearchStatus { Completed, Cancelled, Partial, Failed, Limited }
public enum SearchIssueCode { Unknown, Encoding, AccessDenied, InvalidData, ResourceLimit, FileChanged }
public enum SourceKind { TextLine, CsvField, WordParagraph, WordTableCell, ExcelCell, SlideText, SlideTableCell, PdfPage }
public enum SearchIssueSeverity { Error, Warning }
/// <summary>BOM always takes precedence. Legacy modes apply only to files without a BOM.</summary>
public enum TextEncodingMode { StrictUnicode, Utf8OrGb18030, Gb18030 }

public sealed record SearchRequest(
    string RootFolder,
    IReadOnlyList<string> Terms,
    SearchOperator Operator = SearchOperator.And,
    IReadOnlyList<string>? Extensions = null,
    bool MatchCase = false,
    int ContextCharacters = 60,
    TextEncodingMode EncodingMode = TextEncodingMode.StrictUnicode)
{
    /// <summary>Literal preserves the existing keyword and expression semantics.</summary>
    public SearchMatchMode MatchMode { get; init; } = SearchMatchMode.Literal;

    /// <summary>A .NET regular expression. RegularExpression mode requires empty Terms and null
    /// Expression/TermConnectors; Literal mode requires null RegexPattern.</summary>
    public string? RegexPattern { get; init; }

    /// <summary>Adjacent joins in original Terms order, with exactly Terms.Count - 1 entries.
    /// Null retains uniform Operator semantics. AND has higher precedence than OR.</summary>
    public IReadOnlyList<SearchOperator>? TermConnectors { get; init; }

    /// <summary>An explicit expression over original Terms positions. Every position must occur exactly once.
    /// Mutually exclusive with TermConnectors; null retains adjacent-connector compatibility.</summary>
    public SearchExpression? Expression { get; init; }
}

/// <summary>All source indices are 1-based. Column and match offsets count UTF-16 code units.
/// Non-text locations describe extracted structures, never an Office range or a rendered Word page.</summary>
public record SourceLocator(
    SourceKind Kind, long Line = 0, int Column = 0,
    long? Record = null, int? Field = null, int? Paragraph = null,
    int? Table = null, int? Row = null, int? Cell = null,
    string? Sheet = null, string? Address = null,
    int? Slide = null, string? ObjectName = null, int? Page = null, string? ValueKind = null);

/// <summary>Compatibility constructor for the first-stage physical-line location.</summary>
public sealed record SourceLocation(long Line, int Column) : SourceLocator(SourceKind.TextLine, Line, Column);

/// <summary>A real paragraph, cell, page, or physical text line. Separate segments are never concatenated.</summary>
public sealed record TextSegment(string Id, string Text, SourceLocator Source);
public sealed record MatchItem(string Term, string Before, string Text, string After, SourceLocator Location,
    string? SourceSegmentId = null, int Offset = 0);
public sealed record FileResult(
    string FullPath, string Extension, DateTime LastWriteTimeUtc, IReadOnlyList<MatchItem> Matches)
{
    public int TotalOccurrences => Matches.Count;
    public IReadOnlyList<string> ExtractionWarnings { get; init; } = [];
}
public sealed record SearchIssue(string Path, string Message, SearchIssueSeverity Severity = SearchIssueSeverity.Error,
    SearchIssueCode Code = SearchIssueCode.Unknown);

/// <summary>ScannedFiles counts eligible completed or failed files, not interrupted files.
/// SkippedFiles counts filtered, temporary, linked and failed files, not directory subtrees.
/// Occurrences counts independent non-overlapping matches per literal term, or nonempty complete
/// regular-expression matches, in emitted files.</summary>
public sealed record SearchProgress(int ScannedFiles, int MatchedFiles, int Occurrences, int SkippedFiles, string? CurrentPath);
/// <summary>Counts original issues, including omitted details, but excludes the optional final aggregate notice.
/// SuppressedIssueCount counts issues omitted by either the global or per-file detail budget exactly once.
/// LimitReason names a global budget and never contains query terms or extracted content.</summary>
public sealed record SearchDiagnostics
{
    public long ErrorCount { get; init; }
    public long WarningCount { get; init; }
    public long SuppressedIssueCount { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string? LimitReason { get; init; }
}

public sealed record SearchSummary(SearchStatus Status, SearchProgress Progress)
{
    public SearchDiagnostics Diagnostics { get; init; } = new();
}
