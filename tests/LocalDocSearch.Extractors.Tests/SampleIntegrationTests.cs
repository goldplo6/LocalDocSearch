using System.Security.Cryptography;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class SampleIntegrationTests(SampleCorpus corpus) : IClassFixture<SampleCorpus>
{
    private static readonly string[] AllExtensions = [".txt", ".md", ".log", ".csv", ".docx", ".xlsx", ".pptx", ".pdf"];

    [Fact]
    public async Task AllEightFormats_AndQuery_ReturnsEightFilesAndSixteenOccurrences()
    {
        var scan = await ExtractionTestSupport.SearchAsync(corpus.Root, ["西瓜", "苹果"]);
        Assert.Equal(8, scan.Results.Count);
        Assert.Equal(16, scan.Summary.Progress.Occurrences);
        Assert.Equal(8, scan.Summary.Progress.ScannedFiles);
        Assert.DoesNotContain(scan.Issues, issue => issue.Severity == SearchIssueSeverity.Error);
        Assert.Equal(AllExtensions.Order(), scan.Results.Select(file => file.Extension).Order());
        Assert.All(scan.Results, file =>
        {
            Assert.Equal(2, file.TotalOccurrences);
            Assert.Single(file.Matches, match => match.Term == "西瓜");
            Assert.Single(file.Matches, match => match.Term == "苹果");
        });
    }

    [Theory]
    [InlineData(".*", 8, 8)]
    [InlineData("隐藏专用词", 0, 0)]
    public async Task SampleQueries_RespectLiteralSymbolsAndHiddenWorksheetScope(string term, int expectedFiles, int expectedOccurrences)
    {
        var scan = await ExtractionTestSupport.SearchAsync(corpus.Root, [term]);
        Assert.Equal(expectedFiles, scan.Results.Count);
        Assert.Equal(expectedOccurrences, scan.Summary.Progress.Occurrences);
        Assert.DoesNotContain(scan.Issues, issue => issue.Severity == SearchIssueSeverity.Error);
    }

    [Fact]
    public async Task OfficeAndPdfLocations_ReferToSavedStructures()
    {
        var word = await ExtractionTestSupport.ReadAsync(corpus.FileFor(".docx"));
        var melon = Assert.Single(word, item => item.Text.Contains("西瓜"));
        Assert.Equal(SourceKind.WordParagraph, melon.Source.Kind);
        Assert.True(melon.Source.Paragraph > 0);
        Assert.Null(melon.Source.Page);
        var apple = Assert.Single(word, item => item.Text.Contains("苹果"));
        Assert.Equal(SourceKind.WordTableCell, apple.Source.Kind);
        Assert.True(apple.Source.Table > 0);
        Assert.True(apple.Source.Row > 0);
        Assert.True(apple.Source.Cell > 0);
        Assert.True(apple.Source.Paragraph > 0);

        var excel = await ExtractionTestSupport.ReadAsync(corpus.FileFor(".xlsx"));
        Assert.Contains(excel, item => item.Text.Contains("西瓜") && item.Source.Sheet == "Sheet2" && item.Source.Address == "B15" && item.Source.Row == 15 && item.Source.Cell == 2);
        Assert.Contains(excel, item => item.Text.Contains("苹果") && item.Source.Sheet == "Sheet2" && item.Source.Address == "D20" && item.Source.Row == 20 && item.Source.Cell == 4);

        var slides = await ExtractionTestSupport.ReadAsync(corpus.FileFor(".pptx"));
        Assert.Equal(1, Assert.Single(slides, item => item.Text.Contains("苹果")).Source.Slide);
        Assert.Equal(8, Assert.Single(slides, item => item.Text.Contains("西瓜")).Source.Slide);
        Assert.All(slides.Where(item => item.Text.Contains("西瓜") || item.Text.Contains("苹果")), item => Assert.False(string.IsNullOrWhiteSpace(item.Source.ObjectName)));

        var pdf = await ExtractionTestSupport.ReadAsync(corpus.FileFor(".pdf"));
        Assert.Equal(3, pdf.Count);
        Assert.Equal(1, Assert.Single(pdf, item => item.Text.Contains("苹果")).Source.Page);
        Assert.Equal(3, Assert.Single(pdf, item => item.Text.Contains("西瓜")).Source.Page);
        Assert.All(pdf, item => Assert.Equal(SourceKind.PdfPage, item.Source.Kind));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".log")]
    [InlineData(".csv")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task EveryFormat_IsReadOnlyAndHasStableUniqueSegmentIds(string extension)
    {
        var path = corpus.FileFor(extension);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var modified = File.GetLastWriteTimeUtc(path);
        var first = await ExtractionTestSupport.ReadAsync(path);
        var second = await ExtractionTestSupport.ReadAsync(path);
        Assert.NotEmpty(first);
        Assert.Equal(first.Count, first.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.Equal(first, second);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task ExistingReadersAndWriters_CanShareInput(string extension)
    {
        var path = corpus.FileFor(extension);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.NotEmpty(await ExtractionTestSupport.ReadAsync(path));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task AlreadyCancelledExtraction_EmitsNoSegments(string extension)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var published = new List<TextSegment>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in ExtractionTestSupport.For(extension).ExtractAsync(corpus.FileFor(extension), new ExtractionContext(_ => { }), cancellation.Token))
                published.Add(item);
        });
        Assert.Empty(published);
    }
}
