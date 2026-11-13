using System.Security.Cryptography;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class EncodingModeTests
{
    [Fact]
    public void ExistingConstructorsDefaultToStrictUnicodeAndContextExposesRequestedMode()
    {
        Assert.Equal(TextEncodingMode.StrictUnicode, new SearchRequest("unused", ["term"]).EncodingMode);
        Assert.Equal(TextEncodingMode.StrictUnicode, new ExtractionContext(_ => { }).EncodingMode);
        Assert.Equal(TextEncodingMode.Gb18030, new ExtractionContext(_ => { }, TextEncodingMode.Gb18030).EncodingMode);
    }

    [Fact]
    public void UnknownRequestModeIsRejectedBeforeSearching()
    {
        using var directory = new SearchTestDirectory();
        var request = new SearchRequest(directory.Root, ["term"], EncodingMode: (TextEncodingMode)99);
        var exception = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(request); });
        Assert.Contains("编码模式", exception.Message);
    }

    [Theory]
    [InlineData(".txt", false)]
    [InlineData(".txt", true)]
    [InlineData(".csv", false)]
    [InlineData(".csv", true)]
    public async Task Utf8AndCorrectlyResavedUtf8RemainSearchableWithoutCompatibility(string extension, bool bom)
    {
        using var directory = new SearchTestDirectory();
        var text = extension == ".csv" ? "编号,说明\r\n1,西瓜𠮷 苹果" : "原创资料\r\n西瓜𠮷 苹果";
        // Simulate a correct Save As by decoding the original bytes before writing a separate UTF-8 file.
        var legacyBytes = StrictGb18030().GetBytes(text);
        var restoredText = StrictGb18030().GetString(legacyBytes);
        await directory.WriteAsync("resaved" + extension, restoredText, new UTF8Encoding(bom, true));
        foreach (var mode in new[] { TextEncodingMode.StrictUnicode, TextEncodingMode.Utf8OrGb18030 })
        {
            var capture = new SearchCapture();
            var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "𠮷", "苹果"], EncodingMode: mode));
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Equal(3, Assert.Single(capture.Results).TotalOccurrences);
            Assert.Empty(capture.Issues);
        }
    }

    public static IEnumerable<object[]> BomCases()
    {
        foreach (var extension in new[] { ".txt", ".csv" })
            foreach (var encoding in new[] { "utf8", "utf16le", "utf16be", "utf32le", "utf32be" })
                foreach (var mode in Enum.GetValues<TextEncodingMode>())
                    yield return [extension, encoding, mode];
    }

    [Theory]
    [MemberData(nameof(BomCases))]
    public async Task UnicodeBomAlwaysWinsOverLegacyModes(string extension, string name, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var encoding = BomEncoding(name);
        await directory.WriteAsync("bom" + extension, "西瓜,苹果", encoding);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["西瓜", "苹果"], EncodingMode: mode));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(2, Assert.Single(capture.Results).TotalOccurrences);
        Assert.Empty(capture.Issues);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task Gb18030RequiresOptInAndFallbackPreservesLocationsAndSourceBytes(string extension)
    {
        using var directory = new SearchTestDirectory();
        var text = extension == ".csv"
            ? "编号,说明\r\n1,\"第一行\r\n西瓜𠮷 苹果\"\r\n2,西瓜"
            : "说明\r\n西瓜𠮷 苹果\r\n西瓜";
        var path = await directory.WriteBytesAsync("legacy" + extension, StrictGb18030().GetBytes(text));
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var modified = File.GetLastWriteTimeUtc(path);
        var strict = new SearchCapture();
        var failed = await strict.RunAsync(new SearchRequest(directory.Root, ["西瓜"]));
        Assert.Equal(SearchStatus.Failed, failed.Status);
        Assert.Empty(strict.Results);
        Assert.Equal(SearchIssueSeverity.Error, Assert.Single(strict.Issues).Severity);

        var compatible = new SearchCapture();
        var summary = await compatible.RunAsync(new SearchRequest(directory.Root, ["西瓜", "𠮷", "苹果"],
            EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(0, summary.Progress.SkippedFiles);
        Assert.Equal(4, summary.Progress.Occurrences);
        var file = Assert.Single(compatible.Results);
        Assert.Equal(modified, file.LastWriteTimeUtc);
        var warning = Assert.Single(compatible.Issues);
        Assert.Equal(path, warning.Path);
        Assert.Equal(SearchIssueSeverity.Warning, warning.Severity);
        Assert.Contains("UTF-8", warning.Message);
        Assert.Contains("GB18030", warning.Message);
        Assert.Contains("回退", warning.Message);
        Assert.DoesNotContain("西瓜", warning.Message);
        Assert.DoesNotContain("第一行", warning.Message);
        Assert.Equal(warning.Message, Assert.Single(file.ExtractionWarnings));
        var first = file.Matches[0];
        if (extension == ".csv")
        {
            Assert.Equal(SourceKind.CsvField, first.Location.Kind);
            Assert.Equal(2L, first.Location.Record);
            Assert.Equal(2, first.Location.Field);
            Assert.Equal(2L, first.Location.Line);
            Assert.Equal("第一行\n", first.Before);
            Assert.Equal("csv:2:2", first.SourceSegmentId);
            Assert.Equal(4L, file.Matches[^1].Location.Line);
        }
        else
        {
            Assert.Equal(new SourceLocation(2, 1), first.Location);
            Assert.Equal("line:2", first.SourceSegmentId);
        }

        var forced = new SearchCapture();
        var forcedSummary = await forced.RunAsync(new SearchRequest(directory.Root, ["西瓜", "𠮷", "苹果"],
            EncodingMode: TextEncodingMode.Gb18030));
        Assert.Equal(SearchStatus.Completed, forcedSummary.Status);
        Assert.Empty(forced.Issues);
        Assert.Equal(file.Matches, Assert.Single(forced.Results).Matches);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task ValidUtf8WinsAndForcedGb18030ResolvesAnAmbiguousFile(string extension)
    {
        using var directory = new SearchTestDirectory();
        byte[] bytes = [0xc2, 0xa9]; // Both strict decoders accept these bytes but produce different text.
        var utf8Text = new UTF8Encoding(false, true).GetString(bytes);
        var gbText = StrictGb18030().GetString(bytes);
        Assert.NotEqual(utf8Text, gbText);
        await directory.WriteBytesAsync("ambiguous" + extension, bytes);
        foreach (var mode in Enum.GetValues<TextEncodingMode>())
        {
            var capture = new SearchCapture();
            var expected = mode == TextEncodingMode.Gb18030 ? gbText : utf8Text;
            var summary = await capture.RunAsync(new SearchRequest(directory.Root, [expected], EncodingMode: mode));
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Equal(expected, Assert.Single(Assert.Single(capture.Results).Matches).Text);
            Assert.Empty(capture.Issues);
        }
        var automatic = new SearchCapture();
        await automatic.RunAsync(new SearchRequest(directory.Root, [gbText], EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Empty(automatic.Results);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task LateUtf8FailureRestartsBeforePublishingAnySegmentsOrDuplicateMatches(string extension)
    {
        using var directory = new SearchTestDirectory();
        var text = "MARKER\n" + new string('x', 40_000) + "\n西瓜";
        var path = await directory.WriteBytesAsync("late-fallback" + extension, StrictGb18030().GetBytes(text));
        var warnings = new List<string>();
        var segments = new List<TextSegment>();
        await foreach (var segment in Extractor(extension).ExtractAsync(path,
            new ExtractionContext(warnings.Add, TextEncodingMode.Utf8OrGb18030)))
            segments.Add(segment);
        Assert.Equal(new[] { "MARKER", new string('x', 40_000), "西瓜" }, segments.Select(segment => segment.Text));
        Assert.Equal(segments.Count, segments.Select(segment => segment.Id).Distinct().Count());
        Assert.Single(warnings);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["MARKER", "西瓜"],
            EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(2, summary.Progress.Occurrences);
        Assert.Equal(2, Assert.Single(capture.Results).TotalOccurrences);
    }

    [Theory]
    [InlineData(".txt", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".txt", TextEncodingMode.Gb18030)]
    [InlineData(".csv", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".csv", TextEncodingMode.Gb18030)]
    public async Task LateInvalidGb18030PublishesNoSegmentsWarningsOrMatches(string extension, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var prefix = StrictGb18030().GetBytes("MARKER\n" + new string('x', 40_000) + "\n西瓜");
        var path = await directory.WriteBytesAsync("late-invalid" + extension, [.. prefix, 0x81]);
        var warnings = new List<string>();
        var published = new List<TextSegment>();
        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
        {
            await foreach (var segment in Extractor(extension).ExtractAsync(path, new ExtractionContext(warnings.Add, mode)))
                published.Add(segment);
        });
        Assert.Empty(published);
        Assert.Empty(warnings);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["MARKER"], EncodingMode: mode));
        Assert.Equal(SearchStatus.Failed, summary.Status);
        Assert.Equal(0, summary.Progress.Occurrences);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.SkippedFiles);
        Assert.Empty(capture.Results);
        Assert.Equal(SearchIssueSeverity.Error, Assert.Single(capture.Issues).Severity);
    }

    public static IEnumerable<object[]> InvalidGbCases()
    {
        foreach (var mode in new[] { TextEncodingMode.Utf8OrGb18030, TextEncodingMode.Gb18030 })
            foreach (var bytes in new byte[][] { [0x81], [0x81, 0x7f], [0x81, 0x30, 0x81] })
                yield return [mode, bytes];
    }

    [Theory]
    [MemberData(nameof(InvalidGbCases))]
    public async Task IllegalGb18030IsRejectedInsteadOfReplacingWithQuestionMarks(TextEncodingMode mode, byte[] bytes)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteBytesAsync("invalid.txt", bytes);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["?"], EncodingMode: mode));
        Assert.Equal(SearchStatus.Failed, summary.Status);
        Assert.Empty(capture.Results);
        Assert.Equal(SearchIssueSeverity.Error, Assert.Single(capture.Issues).Severity);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task ExistingDamageAndLiteralQuestionMarksArePreservedWithoutInventingText(string extension)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("damaged" + extension, "损坏??与� 西?瓜");
        var literal = new SearchCapture();
        var summary = await literal.RunAsync(new SearchRequest(directory.Root, ["??", "�", "西?瓜"],
            EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(new[] { "??", "�", "西?瓜" }, Assert.Single(literal.Results).Matches.Select(match => match.Text));
        Assert.Empty(literal.Issues);
        var invented = new SearchCapture();
        await invented.RunAsync(new SearchRequest(directory.Root, ["西瓜"], EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Empty(invented.Results);
    }

    [Theory]
    [InlineData(".txt", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".txt", TextEncodingMode.Gb18030)]
    [InlineData(".csv", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".csv", TextEncodingMode.Gb18030)]
    public async Task PreCancelledExtractionStopsBeforeOpeningOrPreReading(string extension, TextEncodingMode mode)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        await using var iterator = Extractor(extension).ExtractAsync(path, new ExtractionContext(_ => { }, mode), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
    }

    [Theory]
    [InlineData(".txt", TextEncodingMode.StrictUnicode)]
    [InlineData(".txt", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".txt", TextEncodingMode.Gb18030)]
    [InlineData(".csv", TextEncodingMode.StrictUnicode)]
    [InlineData(".csv", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".csv", TextEncodingMode.Gb18030)]
    public async Task CancellationBetweenBufferedSegmentsStopsImmediately(string extension, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("segments" + extension, "first\nsecond\nthird");
        using var cancellation = new CancellationTokenSource();
        await using var iterator = Extractor(extension).ExtractAsync(path, new ExtractionContext(_ => { }, mode), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("first", iterator.Current.Text);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task CancellationAfterFallbackValidationPublishesNoResults(string extension)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("legacy" + extension, "西瓜\n苹果", StrictGb18030());
        using var cancellation = new CancellationTokenSource();
        var results = new List<FileResult>();
        var warnings = new List<SearchIssue>();
        var summary = await new TextSearchEngine().SearchAsync(new SearchRequest(directory.Root, ["西瓜"],
            EncodingMode: TextEncodingMode.Utf8OrGb18030),
            results: new InlineProgress<FileResult>(results.Add),
            issues: new InlineProgress<SearchIssue>(issue => { warnings.Add(issue); cancellation.Cancel(); }),
            cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Empty(results);
        Assert.Equal(0, summary.Progress.ScannedFiles);
        Assert.Equal(0, summary.Progress.Occurrences);
        Assert.Equal(SearchIssueSeverity.Warning, Assert.Single(warnings).Severity);
    }

    [Theory]
    [InlineData(TextEncodingMode.Utf8OrGb18030)]
    [InlineData(TextEncodingMode.Gb18030)]
    public async Task InvalidUnicodeBomNeverFallsBackToGb18030(TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteBytesAsync("invalid-bom.csv", [0xef, 0xbb, 0xbf, 0x81, 0x40]);
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["?"], EncodingMode: mode));
        Assert.Equal(SearchStatus.Failed, summary.Status);
        Assert.Empty(capture.Results);
        Assert.Equal(SearchIssueSeverity.Error, Assert.Single(capture.Issues).Severity);
    }

    [Fact]
    public async Task FallbackWarningWithoutAnyMatchStillExplainsPartialStatus()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("legacy.txt", "原创编码样本", StrictGb18030());
        var capture = new SearchCapture();
        var summary = await capture.RunAsync(new SearchRequest(directory.Root, ["missing"], EncodingMode: TextEncodingMode.Utf8OrGb18030));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Empty(capture.Results);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(0, summary.Progress.SkippedFiles);
        Assert.Equal(SearchIssueSeverity.Warning, Assert.Single(capture.Issues).Severity);
    }

    private static ITextExtractor Extractor(string extension) => extension == ".csv" ? new CsvTextExtractor() : new PlainTextExtractor();

    private static Encoding StrictGb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static Encoding BomEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(true, true),
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        _ => new UTF32Encoding(true, true, true)
    };
}
