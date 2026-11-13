using System.Text;
using LocalDocSearch.Core;

namespace LocalDocSearch.Extractors;

/// <summary>Checks logical UTF-16 limits before appending; parser-owned strings and
/// StringBuilder capacity/ToString copies are not a hard process-memory ceiling.</summary>
internal sealed class BoundedTextBuilder
{
    private readonly StringBuilder _text = new();
    private readonly int _maxSegmentCharacters;
    private readonly long _remainingCharacters;

    internal BoundedTextBuilder(int maxSegmentCharacters, long remainingCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingCharacters);
        _maxSegmentCharacters = maxSegmentCharacters;
        _remainingCharacters = remainingCharacters;
    }

    internal void Append(string? value)
    {
        if (value is null) return;
        Check(value.Length);
        _text.Append(value);
    }

    internal void Append(char value)
    {
        Check(1);
        _text.Append(value);
    }

    private void Check(int additionalCharacters)
    {
        if (additionalCharacters > _maxSegmentCharacters - _text.Length)
            throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), _maxSegmentCharacters);
        if (additionalCharacters > _remainingCharacters - _text.Length)
            throw new ExtractionException("文档累计提取内容超过字符上限，已停止构建文字；未截断或发布部分文档。");
    }

    public override string ToString() => _text.ToString();
}
