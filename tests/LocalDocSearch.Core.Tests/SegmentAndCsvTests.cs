using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class SegmentAndCsvTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task CsvQuotedMultilineFieldsKeepLogicalAndPhysicalLocations(string newline)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("含空格 样本.csv", $"名称,说明{newline}1,\"第一行{newline}西瓜汁\"{newline}2,苹果");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]));
        var file = Assert.Single(capture.Results);
        Assert.Equal(2, file.TotalOccurrences);
        var first = file.Matches[0];
        Assert.Equal(SourceKind.CsvField, first.Location.Kind);
        Assert.Equal(2L, first.Location.Record);
        Assert.Equal(2, first.Location.Field);
        Assert.Equal(2L, first.Location.Line);
        Assert.Equal("第一行\n", first.Before);
        Assert.Equal(4, first.Offset);
        Assert.Equal("csv:2:2", first.SourceSegmentId);
        Assert.Equal(3L, file.Matches[1].Location.Record);
        Assert.Equal(4L, file.Matches[1].Location.Line);
    }

    [Fact]
    public async Task CsvUnescapesQuotesAndDoesNotJoinAdjacentFields()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("quotes.csv", "西,瓜\n\"逗号,和\"\"引号\"\"与西瓜\",苹果\n");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]));
        var file = Assert.Single(capture.Results);
        Assert.Equal(2, file.TotalOccurrences);
        Assert.Equal("逗号,和\"引号\"与", file.Matches[0].Before);
        Assert.Equal(2L, file.Matches[0].Location.Record);
    }

    [Theory]
    [InlineData("西瓜\n\"未闭合")]
    [InlineData("西瓜\n\"已闭合\"尾随")]
    [InlineData("西瓜\n不合法\"引号")]
    public async Task CsvMalformedLateRecordDiscardsAllEarlierMatches(string contents)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("bad.csv", contents);
        await directory.WriteAsync("good.txt", "西瓜");
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(".txt", Assert.Single(capture.Results).Extension);
        Assert.Contains("CSV 格式错误", Assert.Single(capture.Issues).Message);
    }

    [Fact]
    public async Task CsvQuotedFieldSpanningReaderBufferIsMatchedOnce()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("large.csv", "\"" + new string('x', 8191) + "西瓜\",苹果");
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]));
        var file = Assert.Single(capture.Results);
        Assert.Equal(2, file.TotalOccurrences);
        Assert.Equal(8191, file.Matches[0].Offset);
    }

    [Fact]
    public async Task CsvUtf16BomAndEmptyFinalFieldAreSupported()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("unicode.csv", "\"西瓜\",,苹果,", new System.Text.UnicodeEncoding(false, true, true));
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"]));
        var file = Assert.Single(capture.Results);
        Assert.Equal(1, file.Matches[0].Location.Field);
        Assert.Equal(3, file.Matches[1].Location.Field);
    }

    [Fact]
    public async Task RegisteredExtractorUsesFileAndAndDoesNotInventCrossSegmentText()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("custom.fake", "fixture");
        var engine = new TextSearchEngine([new SyntheticExtractor()]);
        var results = new List<FileResult>();
        await engine.SearchAsync(new SearchRequest(directory.Root, ["西", "瓜"]), results: new InlineProgress<FileResult>(results.Add));
        var file = Assert.Single(results);
        Assert.Equal(new[] { "first", "second" }, file.Matches.Select(m => m.SourceSegmentId));
        results.Clear();
        await engine.SearchAsync(new SearchRequest(directory.Root, ["西瓜"]), results: new InlineProgress<FileResult>(results.Add));
        Assert.Empty(results);
    }

    [Fact]
    public async Task WarningWithoutMatchesRemainsVisibleAndMarksSearchPartial()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("custom.fake", "fixture");
        var warnings = new List<SearchIssue>();
        var engine = new TextSearchEngine([new SyntheticExtractor()]);
        var summary = await engine.SearchAsync(new SearchRequest(directory.Root, ["missing"]),
            issues: new InlineProgress<SearchIssue>(warnings.Add));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(SearchIssueSeverity.Warning, Assert.Single(warnings).Severity);
        Assert.Equal(0, summary.Progress.SkippedFiles);
    }

    private sealed class SyntheticExtractor : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".fake" };
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            context.Warn("fixture warning");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextSegment("first", "西", new SourceLocator(SourceKind.WordParagraph, Paragraph: 1));
            yield return new TextSegment("second", "瓜", new SourceLocator(SourceKind.WordParagraph, Paragraph: 2));
        }
    }
}
