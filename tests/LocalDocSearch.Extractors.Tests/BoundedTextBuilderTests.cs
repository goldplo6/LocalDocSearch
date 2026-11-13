using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LocalDocSearch.Extractors.Tests;

public sealed class BoundedTextBuilderTests
{
    [Fact]
    public void ExactBoundary_CountsUtf16AcrossAppendsAndKeepsContentUnchangedAfterFailure()
    {
        var text = new BoundedTextBuilder(maxSegmentCharacters: 4, remainingCharacters: 100);
        text.Append("🍉");
        text.Append('西');
        text.Append('瓜');
        Assert.Equal("🍉西瓜", text.ToString());
        var error = Assert.Throws<SearchResourceLimitException>(() => text.Append('x'));
        Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), error.LimitName);
        Assert.Equal(4, error.Limit);
        Assert.Equal("🍉西瓜", text.ToString());
    }

    [Fact]
    public void CumulativeRemainingBudget_IsCheckedBeforeAppendingAnyOfAnOversizedValue()
    {
        var text = new BoundedTextBuilder(maxSegmentCharacters: 100, remainingCharacters: 3);
        text.Append("ok");
        var error = Assert.Throws<ExtractionException>(() => text.Append("no"));
        Assert.Contains("累计", error.Message);
        Assert.Equal("ok", text.ToString());
        text.Append('!');
        Assert.Equal("ok!", text.ToString());
    }

    [Fact]
    public void EmptyAndNullAppends_DoNotConsumeAZeroRemainingBudget()
    {
        var text = new BoundedTextBuilder(10, 0);
        text.Append((string?)null);
        text.Append(string.Empty);
        Assert.Equal(string.Empty, text.ToString());
        Assert.Throws<ExtractionException>(() => text.Append('x'));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidBudgets_AreRejected(int segment, long remaining) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedTextBuilder(segment, remaining));

    [Fact]
    public void OutputBufferRemainingBudget_ReflectsAlreadyRetainedSegments()
    {
        var output = new BoundedSegmentBuffer(maxCharacters: 4);
        output.Add(new TextSegment("first", "西瓜", new SourceLocator(SourceKind.WordParagraph, Paragraph: 1)));
        Assert.Equal(2L, output.RemainingCharacters);
        var text = new BoundedTextBuilder(100, output.RemainingCharacters);
        text.Append("苹果");
        Assert.Throws<ExtractionException>(() => text.Append('x'));
        Assert.Equal("苹果", text.ToString());
    }

    [Fact]
    public async Task Excel_UnreferencedSharedStringDoesNotConsumeThePerResultSegmentLimit()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("unused-shared-string.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());
            workbook.AddNewPart<SharedStringTablePart>().SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Text("unused long lookup value")));
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell
                {
                    CellReference = "A1", DataType = S.CellValues.InlineString,
                    InlineString = new S.InlineString(new S.Run(new S.Text("o")), new S.Run(new S.Text("k")))
                })));
            workbook.Workbook.Sheets!.Append(new S.Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1U, Name = "Data" });
        }
        var output = new List<TextSegment>();
        var context = new ExtractionContext(_ => { }, limits: new SearchResourceLimits { MaxSegmentCharacters = 2 });
        await foreach (var segment in new ExcelTextExtractor().ExtractAsync(path, context)) output.Add(segment);
        var actual = Assert.Single(output);
        Assert.Equal("ok", actual.Text);
        Assert.Equal("excel:s1:A1", actual.Id);
    }
}
