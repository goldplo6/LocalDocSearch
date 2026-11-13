using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class BoundedExtractorTests
{
    [Fact]
    public async Task PhysicalLinesPreserveEmptyLinesAndDoNotInventATrailingLine()
    {
        var cases = new Dictionary<string, string[]>
        {
            [""] = [], ["\n"] = [""], ["\r\n"] = [""], ["\r"] = [""],
            ["\n\n"] = ["", ""], ["\r\n\rx\n"] = ["", "", "x"],
            ["a\rb\nc\r\nd"] = ["a", "b", "c", "d"]
        };
        using var directory = new SearchTestDirectory();
        foreach (var (text, expected) in cases)
        {
            var path = await directory.WriteAsync("lines.txt", text);
            var segments = await ReadAsync(new PlainTextExtractor(), path, 1);
            Assert.Equal(expected, segments.Select(segment => segment.Text));
            Assert.Equal(Enumerable.Range(1, expected.Length).Select(index => $"line:{index}"), segments.Select(segment => segment.Id));
            Assert.Equal(Enumerable.Range(1, expected.Length).Select(index => (long)index), segments.Select(segment => segment.Source.Line));
        }
    }

    [Theory]
    [InlineData(8190)]
    [InlineData(8191)]
    [InlineData(8192)]
    public async Task PhysicalLineReaderPreservesSurrogatesAndCrLfAcrossBuffers(int prefix)
    {
        using var directory = new SearchTestDirectory();
        var first = new string('x', prefix) + "😀a";
        var path = await directory.WriteAsync("boundary.txt", first + "\r\n\rZ\n");
        var segments = await ReadAsync(new PlainTextExtractor(), path, 10_000);
        Assert.Equal(new[] { first, "", "Z" }, segments.Select(segment => segment.Text));
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine(limits: new() { MaxSegmentCharacters = 10_000 }).SearchAsync(
            new(directory.Root, ["😀a"]), results: new InlineProgress<FileResult>(results.Add));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        var match = Assert.Single(Assert.Single(results).Matches);
        Assert.Equal(prefix, match.Offset);
        Assert.Equal(prefix + 1, match.Location.Column);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task ExactlyFullLineAllowsItsTerminatorAndASecondLine(string newline)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("exact.txt", "aaaa" + newline + "a");
        var segments = await ReadAsync(new PlainTextExtractor(), path, 4);
        Assert.Equal(new[] { "aaaa", "a" }, segments.Select(segment => segment.Text));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task LongSegmentThrowsTypedLimitWithoutEmittingItsPrefixAndReleasesTheFile(string extension)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("long" + extension, new string('x', 100_000));
        var emitted = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<SearchResourceLimitException>(async () =>
        {
            await foreach (var segment in Extractor(extension).ExtractAsync(path, new ExtractionContext(_ => { }, limits: new() { MaxSegmentCharacters = 8192 })))
                emitted.Add(segment);
        });
        Assert.Empty(emitted);
        Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), error.LimitName);
        Assert.Equal(8192, error.Limit);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(100_000, exclusive.Length);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task CsvCountsNormalizedLfBeforeAppendingItAcrossTheCharacterBuffer(string newline)
    {
        using var directory = new SearchTestDirectory();
        // Opening quote + prefix places CR at the final character of CsvCharReader's buffer.
        var prefix = new string('x', 8190);
        var path = await directory.WriteAsync("newlines.csv", "\"" + prefix + newline + "a\",z");
        var segments = await ReadAsync(new CsvTextExtractor(), path, 8192);
        Assert.Equal(prefix + "\na", segments[0].Text);
        Assert.Equal("z", segments[1].Text);
        Assert.Equal(2L, segments[1].Source.Line);
        var error = await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(new CsvTextExtractor(), path, 8191));
        Assert.Equal(8191, error.Limit);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public async Task CsvNormalizedNewlineItselfTriggersTheLimitBeforeAClosingQuote(string newline)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("newline-over.csv", "\"aa" + newline + "\"");
        Assert.Equal("aa\n", Assert.Single(await ReadAsync(new CsvTextExtractor(), path, 3)).Text);
        await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(new CsvTextExtractor(), path, 2));
    }

    [Fact]
    public async Task CsvEscapedQuoteCrossingBufferCountsAsOneUtf16Character()
    {
        using var directory = new SearchTestDirectory();
        var prefix = new string('x', 8190);
        var path = await directory.WriteAsync("quotes.csv", "\"" + prefix + "\"\"a\"");
        var segments = await ReadAsync(new CsvTextExtractor(), path, 8192);
        Assert.Equal(prefix + "\"a", Assert.Single(segments).Text);
        await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(new CsvTextExtractor(), path, 8191));
        var shortPath = await directory.WriteAsync("quote-over.csv", "\"aa\"\"\"");
        await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(new CsvTextExtractor(), shortPath, 2));
    }

    [Theory]
    [InlineData(".txt", false)]
    [InlineData(".txt", true)]
    [InlineData(".csv", false)]
    [InlineData(".csv", true)]
    public async Task BomAndGb18030StillUseUtf16SegmentLimits(string extension, bool legacy)
    {
        using var directory = new SearchTestDirectory();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = legacy ? Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : new UnicodeEncoding(false, true, true);
        var mode = legacy ? TextEncodingMode.Gb18030 : TextEncodingMode.Utf8OrGb18030;
        var text = "西瓜😀"; // Four UTF-16 code units in either encoding.
        var path = await directory.WriteAsync("encoded" + extension, text, encoding);
        var segments = await ReadAsync(Extractor(extension), path, 4, mode);
        Assert.Equal(text, Assert.Single(segments).Text);
        await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(Extractor(extension), path, 3, mode));
    }

    [Theory]
    [InlineData(".txt", TextEncodingMode.StrictUnicode)]
    [InlineData(".txt", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".txt", TextEncodingMode.Gb18030)]
    [InlineData(".csv", TextEncodingMode.StrictUnicode)]
    [InlineData(".csv", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".csv", TextEncodingMode.Gb18030)]
    public async Task CancellationBetweenBufferedSegmentsReleasesReadHandle(string extension, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("cancel" + extension, "first\nsecond\nthird");
        using var cancellation = new CancellationTokenSource();
        await using var iterator = Extractor(extension).ExtractAsync(path, new ExtractionContext(_ => { }, mode), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task EncodingFallbackWarningHasATypedEncodingCode()
    {
        using var directory = new SearchTestDirectory();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await directory.WriteAsync("legacy.txt", "西瓜", Encoding.GetEncoding(54936));
        var issues = new List<SearchIssue>();
        var summary = await new TextSearchEngine().SearchAsync(new(directory.Root, ["西瓜"], EncodingMode: TextEncodingMode.Utf8OrGb18030),
            issues: new InlineProgress<SearchIssue>(issues.Add));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(SearchIssueCode.Encoding, Assert.Single(issues).Code);
    }

    private static ITextExtractor Extractor(string extension) => extension == ".csv" ? new CsvTextExtractor() : new PlainTextExtractor();

    private static async Task<List<TextSegment>> ReadAsync(ITextExtractor extractor, string path, int maxCharacters,
        TextEncodingMode mode = TextEncodingMode.StrictUnicode)
    {
        var result = new List<TextSegment>();
        await foreach (var segment in extractor.ExtractAsync(path, new ExtractionContext(_ => { }, mode, new() { MaxSegmentCharacters = maxCharacters })))
            result.Add(segment);
        return result;
    }
}
