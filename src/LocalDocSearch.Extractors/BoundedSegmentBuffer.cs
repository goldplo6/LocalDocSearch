using LocalDocSearch.Core;

namespace LocalDocSearch.Extractors;

/// <summary>Whole-file atomicity with explicit cumulative output limits, never truncation.</summary>
internal sealed class BoundedSegmentBuffer
{
    internal const long DefaultMaxCharacters = 32L * 1024 * 1024;
    internal const int DefaultMaxSegments = 200_000;
    private readonly List<TextSegment> _segments = [];
    private readonly long _maxCharacters;
    private readonly int _maxSegments;
    private readonly int? _maxSegmentCharacters;
    private long _characters;

    internal BoundedSegmentBuffer(long maxCharacters = DefaultMaxCharacters, int maxSegments = DefaultMaxSegments,
        int? maxSegmentCharacters = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegments);
        if (maxSegmentCharacters.HasValue) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentCharacters.Value);
        _maxCharacters = maxCharacters;
        _maxSegments = maxSegments;
        _maxSegmentCharacters = maxSegmentCharacters;
    }

    internal void Add(TextSegment segment)
    {
        if (_maxSegmentCharacters is { } limit && segment.Text.Length > limit)
            throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
        if (_segments.Count >= _maxSegments || segment.Text.Length > _maxCharacters - _characters)
            throw new ExtractionException($"文档累计提取内容超过上限（{_maxCharacters:N0} 个 UTF-16 字符或 {_maxSegments:N0} 个片段），已停止提取；未截断或发布部分文档。");
        _characters += segment.Text.Length;
        _segments.Add(segment);
    }

    internal long RemainingCharacters => _maxCharacters - _characters;

    internal List<TextSegment> Complete() => _segments;
}
