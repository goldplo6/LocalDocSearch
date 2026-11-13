using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class BoundedSegmentBufferTests
{
    [Fact]
    public void CharacterBudget_IsCumulativeAndAllowsExactBoundary()
    {
        var buffer = new BoundedSegmentBuffer(maxCharacters: 4, maxSegments: 3);
        buffer.Add(Segment("a", "西瓜"));
        buffer.Add(Segment("b", "苹果"));
        var error = Assert.Throws<ExtractionException>(() => buffer.Add(Segment("c", "x")));
        Assert.Contains("UTF-16", error.Message);
        Assert.Contains("未截断", error.Message);
        Assert.Equal(new[] { "西瓜", "苹果" }, buffer.Complete().Select(item => item.Text));
    }

    [Fact]
    public void SegmentBudget_IsIndependentOfCharacterBudget()
    {
        var buffer = new BoundedSegmentBuffer(maxCharacters: 100, maxSegments: 2);
        buffer.Add(Segment("a", "西"));
        buffer.Add(Segment("b", "瓜"));
        Assert.Throws<ExtractionException>(() => buffer.Add(Segment("c", "苹")));
        Assert.Equal(2, buffer.Complete().Count);
    }

    [Fact]
    public void OneOversizedSegmentFailsBeforeAnyAddition()
    {
        var buffer = new BoundedSegmentBuffer(maxCharacters: 1, maxSegments: 2);
        Assert.Throws<ExtractionException>(() => buffer.Add(Segment("a", "西瓜")));
        Assert.Empty(buffer.Complete());
    }

    [Fact]
    public void CharacterBudget_CountsUtf16UnitsIncludingSurrogatePairs()
    {
        var buffer = new BoundedSegmentBuffer(maxCharacters: 2, maxSegments: 2);
        buffer.Add(Segment("a", "🍉"));
        Assert.Throws<ExtractionException>(() => buffer.Add(Segment("b", "x")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidBudgetsAreRejected(int characters, int segments) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSegmentBuffer(characters, segments));

    private static TextSegment Segment(string id, string text) =>
        new(id, text, new SourceLocator(SourceKind.WordParagraph, Paragraph: 1));
}
