using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop;

public sealed record MatchPreviewRequest(string FullPath, MatchItem Match, DateTime LastWriteTimeUtc,
    TextEncodingMode EncodingMode, DocumentScopeOptions Scope);

public sealed record MatchPreviewData(string Text, int SelectionStart, int SelectionLength, string Position,
    bool IsClipped, int WarningCount);

/// <summary>Re-extracts the exact segment, never searches for a different occurrence of the term.</summary>
public sealed class MatchPreviewLoader
{
    public const int MaximumPreviewCharacters = 24_000;
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private readonly IReadOnlyList<ITextExtractor>? _extractors;
    public MatchPreviewLoader(IEnumerable<ITextExtractor>? extractors = null) => _extractors = extractors?.ToArray();

    public async Task<MatchPreviewData> LoadAsync(MatchPreviewRequest request, CancellationToken token = default)
    {
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Cancellation cannot release a parser that is still inside a synchronous Open/GetPage.
            // Keep ownership until the complete enumeration (including disposal) has returned.
            token.ThrowIfCancellationRequested();
            return await LoadCoreAsync(request, token).ConfigureAwait(false);
        }
        finally { ExtractionGate.Release(); }
    }

    private async Task<MatchPreviewData> LoadCoreAsync(MatchPreviewRequest request, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(request.FullPath) || string.IsNullOrEmpty(request.Match.SourceSegmentId) ||
            request.Match.Text.Length == 0 || request.Match.Offset < 0)
            throw new IOException("此结果缺少可核对的来源信息，请重新搜索真实文件。");
        var before = ReadState(request.FullPath);
        if (before.Modified != request.LastWriteTimeUtc)
            throw new IOException("源文件已在搜索后修改，请重新搜索后定位。");
        var extractor = (_extractors ?? DocumentExtractorCatalog.CreateDefault(request.Scope))
            .FirstOrDefault(e => e.Extensions.Contains(Path.GetExtension(request.FullPath), StringComparer.OrdinalIgnoreCase))
            ?? throw new IOException("该文件格式没有文字预览提取器。");
        TextSegment? target = null;
        var warnings = 0;
        var context = new ExtractionContext(_ => warnings++, request.EncodingMode);
        // Finish the iterator before publishing: corruption or cancellation later in the file
        // must not turn an earlier segment into an apparently successful preview.
        await foreach (var segment in extractor.ExtractAsync(request.FullPath, context, token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (segment.Text.Length > context.Limits.MaxSegmentCharacters)
                throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), context.Limits.MaxSegmentCharacters);
            if (segment.Id != request.Match.SourceSegmentId) continue;
            if (target is not null) throw new IOException("来源片段编号重复，请重新搜索。");
            target = segment;
        }
        token.ThrowIfCancellationRequested();
        if (before != ReadState(request.FullPath)) throw new IOException("读取期间源文件发生变化，请重新搜索。");
        if (target is null || !IsSameOccurrence(target, request.Match))
            throw new IOException("命中的原始片段、位置或上下文已变化，请重新搜索；未改选其它同名文字。");
        var start = Math.Max(0, request.Match.Offset - MaximumPreviewCharacters / 2);
        var length = Math.Min(MaximumPreviewCharacters, target.Text.Length - start);
        // Terms are bounded by the search policy, but validate injected requests too.
        if (request.Match.Text.Length > MaximumPreviewCharacters || request.Match.Offset - start + request.Match.Text.Length > length)
            throw new IOException("该命中超出文字预览长度限制。");
        return new MatchPreviewData(target.Text.Substring(start, length), request.Match.Offset - start,
            request.Match.Text.Length, SourcePositionFormatter.Format(request.Match.Location),
            start != 0 || length != target.Text.Length, warnings);
    }

    private static bool IsSameOccurrence(TextSegment segment, MatchItem match)
    {
        var offset = match.Offset;
        var originalLocation = match.Location.Kind == SourceKind.TextLine
            ? match.Location with { Column = segment.Source.Column } : match.Location;
        if (segment.Source != originalLocation || offset > segment.Text.Length - match.Text.Length ||
            offset < match.Before.Length || segment.Text.Length - offset - match.Text.Length < match.After.Length)
            return false;
        return segment.Text.AsSpan(offset, match.Text.Length).SequenceEqual(match.Text.AsSpan()) &&
            segment.Text.AsSpan(offset - match.Before.Length, match.Before.Length).SequenceEqual(match.Before.AsSpan()) &&
            segment.Text.AsSpan(offset + match.Text.Length, match.After.Length).SequenceEqual(match.After.AsSpan());
    }

    private static (long Length, DateTime Modified) ReadState(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("源文件不存在，请重新搜索。");
        for (FileSystemInfo? entry = file; entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("来源路径包含联接或符号链接，请重新选择普通文件目录。");
        return (file.Length, file.LastWriteTimeUtc);
    }
}
