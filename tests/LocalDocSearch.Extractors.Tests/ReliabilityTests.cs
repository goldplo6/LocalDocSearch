using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Extractors.Tests;

public sealed class ReliabilityTests(SampleCorpus corpus) : IClassFixture<SampleCorpus>
{
    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task CorruptOfficeOrPdf_IsWrappedAndIsolatedFromOtherFiles(string extension)
    {
        using var directory = new TestDirectory();
        var bad = directory.FileFor("broken" + extension);
        await File.WriteAllBytesAsync(bad, Encoding.UTF8.GetBytes("this is not an Office or PDF package"));
        await File.WriteAllTextAsync(directory.FileFor("good.txt"), "西瓜和苹果");
        await Assert.ThrowsAsync<ExtractionException>(() => ExtractionTestSupport.ReadAsync(bad));
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["西瓜", "苹果"]);
        Assert.Equal(SearchStatus.Partial, scan.Summary.Status);
        Assert.Single(scan.Results);
        Assert.Equal(2, scan.Summary.Progress.Occurrences);
        Assert.Single(scan.Issues, issue => issue.Path == bad && issue.Severity == SearchIssueSeverity.Error);
    }

    [Fact]
    public async Task MalformedLateWorksheet_DoesNotPublishPreviouslyParsedSegments()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("late-error.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.String, CellValue = new S.CellValue("西瓜苹果") },
                new S.Cell { CellReference = "B1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("9999999") })));
            workbook.Workbook.Sheets!.Append(new S.Sheet { Name = "Sheet1", SheetId = 1U, Id = workbook.GetIdOfPart(worksheet) });
        }
        var published = new List<TextSegment>();
        await Assert.ThrowsAsync<ExtractionException>(async () =>
        {
            await foreach (var item in new ExcelTextExtractor().ExtractAsync(path, new ExtractionContext(_ => { }))) published.Add(item);
        });
        Assert.Empty(published);
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["西瓜", "苹果"]);
        Assert.Empty(scan.Results);
        Assert.Equal(0, scan.Summary.Progress.Occurrences);
        Assert.Equal(SearchStatus.Failed, scan.Summary.Status);
    }

    [Fact]
    public async Task Pdf_OneSegmentPerTextPageAndExplicitWarningForEmptyPage()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("with-empty-page.pdf");
        using (var builder = new PdfDocumentBuilder())
        {
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(600, 800).AddText("apple", 12, new PdfPoint(40, 700), font);
            builder.AddPage(600, 800);
            builder.AddPage(600, 800).AddText("melon", 12, new PdfPoint(40, 700), font);
            await File.WriteAllBytesAsync(path, builder.Build());
        }
        var warnings = new List<string>();
        var segments = await ExtractionTestSupport.ReadAsync(path, warnings);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new int?[] { 1, 3 }, segments.Select(item => item.Source.Page));
        Assert.Contains(warnings, message => message.Contains("第 2 页") && message.Contains("OCR"));
        Assert.Contains(warnings, message => message.Contains("多栏"));
        Assert.All(segments, item => Assert.Equal(SourceKind.PdfPage, item.Source.Kind));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task CancellationDuringSynchronousParsing_EmitsNothingAndRemainsCancellation(string extension)
    {
        using var cancellation = new CancellationTokenSource();
        var published = new List<TextSegment>();
        var context = new ExtractionContext(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in ExtractionTestSupport.For(extension).ExtractAsync(corpus.FileFor(extension), context, cancellation.Token))
                published.Add(item);
        });
        Assert.Empty(published);
    }

    [Fact]
    public async Task SearchCancellationAfterFirstSegment_DoesNotPublishUnfinishedFile()
    {
        using var directory = new TestDirectory();
        File.Copy(corpus.FileFor(".docx"), directory.FileFor("cancel.docx"));
        using var cancellation = new CancellationTokenSource();
        var extractor = new CancelAfterFirstExtractor(new WordTextExtractor(), cancellation);
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["西瓜"], extractors: [extractor], token: cancellation.Token);
        Assert.Equal(1, extractor.PublishedSegments);
        Assert.Equal(SearchStatus.Cancelled, scan.Summary.Status);
        Assert.Empty(scan.Results);
        Assert.Equal(0, scan.Summary.Progress.ScannedFiles);
        Assert.Equal(0, scan.Summary.Progress.Occurrences);
    }

    [Fact]
    public async Task ExtensionFilter_IsAppliedBeforeCallingParser()
    {
        using var directory = new TestDirectory();
        await File.WriteAllTextAsync(directory.FileFor("good.txt"), "西瓜苹果");
        await File.WriteAllTextAsync(directory.FileFor("never-parse.pdf"), "corrupt PDF");
        var probe = new NeverParseExtractor();
        var extractors = DocumentExtractorCatalog.CreateDefault().Where(item => item is not PdfTextExtractor).Append(probe);
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["西瓜", "苹果"], [".txt"], extractors);
        Assert.Equal(0, probe.Calls);
        Assert.Single(scan.Results);
        Assert.Equal(1, scan.Summary.Progress.ScannedFiles);
        Assert.Empty(scan.Issues);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task OversizedInputFailsExplicitlyBeforeParsing(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("oversize" + extension);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(128L * 1024 * 1024 + 1);
        var error = await Assert.ThrowsAsync<ExtractionException>(() => ExtractionTestSupport.ReadAsync(path));
        Assert.Contains("128 MiB", error.Message);
    }

    [Fact]
    public async Task OversizedOpenXmlPartFailsWithoutSilentlyTruncating()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("large-part.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(new string('x', 16 * 1024 * 1024 + 1))))));
        await Assert.ThrowsAsync<ExtractionException>(() => ExtractionTestSupport.ReadAsync(path));
    }

    private sealed class NeverParseExtractor : ITextExtractor
    {
        public int Calls { get; private set; }
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".pdf" };
        public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Filtered format must not reach its parser.");
        }
    }

    private sealed class CancelAfterFirstExtractor(ITextExtractor inner, CancellationTokenSource cancellation) : ITextExtractor
    {
        public int PublishedSegments { get; private set; }
        public IReadOnlyCollection<string> Extensions => inner.Extensions;
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var segment in inner.ExtractAsync(path, context, cancellationToken))
            {
                PublishedSegments++;
                yield return segment;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
