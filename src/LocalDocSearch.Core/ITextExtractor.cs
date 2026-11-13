namespace LocalDocSearch.Core;

public interface ITextExtractor
{
    IReadOnlyCollection<string> Extensions { get; }
    IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default);
}

public sealed class ExtractionContext(Action<string> reportWarning,
    TextEncodingMode encodingMode = TextEncodingMode.StrictUnicode, SearchResourceLimits? limits = null)
{
    private readonly Action<string, SearchIssueCode>? _typedWarning;

    internal ExtractionContext(Action<string, SearchIssueCode> reportWarning, TextEncodingMode encodingMode, SearchResourceLimits limits)
        : this(message => reportWarning(message, SearchIssueCode.Unknown), encodingMode, limits) => _typedWarning = reportWarning;

    public TextEncodingMode EncodingMode { get; } = encodingMode;
    public SearchResourceLimits Limits { get; } = (limits ?? new()).Snapshot();
    public void Warn(string message) => Warn(message, SearchIssueCode.Unknown);
    internal void Warn(string message, SearchIssueCode code)
    {
        if (_typedWarning is { } typed) typed(message, code);
        else reportWarning(message);
    }
}

/// <summary>Expected parser failures are isolated to one file by the search engine.</summary>
public class ExtractionException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>A single file exceeded a logical extraction or match budget; no part of it is published.</summary>
public sealed class SearchResourceLimitException(string limitName, int limit)
    : ExtractionException($"文件资源限额 {limitName}={limit} 已超出；该文件的结果已全部丢弃。")
{
    public string LimitName { get; } = limitName;
    public int Limit { get; } = limit;
}
