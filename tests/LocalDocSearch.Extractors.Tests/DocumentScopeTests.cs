using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Extractors.Tests;

public sealed class DocumentScopeTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Word_ScopesRetainStructuralIdsAndNestedTableBoundaries(bool body, bool tables)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("scope.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                Paragraph("正文苹果"), Paragraph("西"), Paragraph("瓜"),
                new W.Table(new W.TableRow(new W.TableCell(Paragraph("表格香蕉"),
                    new W.Table(new W.TableRow(new W.TableCell(Paragraph("嵌套橙子")))), Paragraph("嵌套之后")))),
                new W.Table(new W.TableRow(new W.TableCell(Paragraph("第二表")))), Paragraph("正文末尾")));
            main.AddNewPart<HeaderPart>().Header = new W.Header(Paragraph("页眉排除"));
            main.AddNewPart<WordprocessingCommentsPart>().Comments = new W.Comments(new W.Comment(Paragraph("批注排除")) { Id = "0" });
        }
        var baseline = await ReadUnchanged(path, new());
        var actual = await ReadUnchanged(path, new(WordBody: body, WordTables: tables));
        Assert.Equal(baseline.Where(segment => segment.Source.Kind == SourceKind.WordParagraph ? body : tables), actual);
        Assert.DoesNotContain(actual, segment => segment.Text.Contains("西瓜") || segment.Text.Contains("排除"));
        Assert.Equal("word:p4", Assert.Single(baseline, segment => segment.Text == "正文末尾").Id);
        Assert.Equal("word:t1:r1:c1:p2", Assert.Single(baseline, segment => segment.Text == "嵌套之后").Id);
        Assert.Equal("word:t2:r1:c1:p1", Assert.Single(baseline, segment => segment.Text == "嵌套橙子").Id);
        Assert.Equal("word:t3:r1:c1:p1", Assert.Single(baseline, segment => segment.Text == "第二表").Id);
        Assert.Equal(actual.Count, actual.Select(segment => segment.Id).Distinct().Count());
        var search = await ExtractionTestSupport.SearchAsync(directory.Root, ["正文苹果"], [".docx"], DocumentExtractorCatalog.CreateDefault(new(WordBody: body, WordTables: tables)));
        Assert.Equal(body ? 1 : 0, search.Results.Count);
        search = await ExtractionTestSupport.SearchAsync(directory.Root, ["表格香蕉"], [".docx"], DocumentExtractorCatalog.CreateDefault(new(WordBody: body, WordTables: tables)));
        Assert.Equal(tables ? 1 : 0, search.Results.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Excel_HiddenAndVeryHiddenAreOptInWithoutRenumberingVisibleSheets(bool includeHidden)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("scope.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());
            AddSheet("隐藏", S.SheetStateValues.Hidden, "B15");
            AddSheet("深度隐藏", S.SheetStateValues.VeryHidden, "C20");
            AddSheet("可见", S.SheetStateValues.Visible, "D25");
            void AddSheet(string name, S.SheetStateValues state, string address)
            {
                var part = workbook.AddNewPart<WorksheetPart>();
                part.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
                {
                    CellReference = address, DataType = S.CellValues.InlineString,
                    InlineString = new S.InlineString(new S.Text(name + "苹果"))
                })));
                workbook.Workbook.Sheets!.Append(new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = (uint)workbook.Workbook.Sheets.ChildElements.Count + 1, Name = name, State = state });
            }
        }
        var baseline = await ReadUnchanged(path, new());
        var actual = await ReadUnchanged(path, new(ExcelHiddenSheets: includeHidden));
        Assert.Equal(includeHidden ? 3 : 1, actual.Count);
        Assert.Equal(Assert.Single(baseline), Assert.Single(actual, item => item.Source.Sheet == "可见"));
        Assert.Equal("excel:s3:D25", Assert.Single(baseline).Id);
        if (includeHidden)
        {
            Assert.Contains(actual, item => item.Id == "excel:s1:B15" && item.Source.Sheet == "隐藏" && item.Source.Address == "B15");
            Assert.Contains(actual, item => item.Id == "excel:s2:C20" && item.Source.Sheet == "深度隐藏" && item.Source.Address == "C20");
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PowerPoint_IndependentScopesUseActualTopLevelObjectPositions(bool text, bool tables)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("scope.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var slide = presentation.AddNewPart<SlidePart>();
            var table = new A.Table(new A.TableRow(
                new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("表格苹果")))), new A.TableCellProperties())) { Height = 1L });
            var frame = new P.GraphicFrame(new P.NonVisualGraphicFrameProperties(new P.NonVisualDrawingProperties { Id = 4U, Name = "表格" }),
                new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(), new P.GroupShapeProperties(), Shape("文本", "正文西瓜"),
                new P.GroupShape(Shape("组合", "组合排除")), new P.Picture(), frame, Shape("末尾", "末尾文本"), new P.ExtensionListWithModification())));
            slide.AddNewPart<NotesSlidePart>().NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(Shape("备注", "备注排除"))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(slide) });
        }
        var baseline = await ReadUnchanged(path, new());
        var actual = await ReadUnchanged(path, new(PresentationText: text, PresentationTables: tables));
        Assert.Equal(baseline.Where(segment => segment.Source.Kind == SourceKind.SlideText ? text : tables), actual);
        Assert.Equal("ppt:s1:o1:r:c:p1", Assert.Single(baseline, item => item.Text == "正文西瓜").Id);
        Assert.Equal("ppt:s1:o4:r1:c1:p1", Assert.Single(baseline, item => item.Text == "表格苹果").Id);
        Assert.Equal("ppt:s1:o5:r:c:p1", Assert.Single(baseline, item => item.Text == "末尾文本").Id);
        Assert.DoesNotContain(actual, item => item.Text.Contains("排除"));
        Assert.Equal(actual.Count, actual.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void DefaultConstructorsAndCatalogRemainCompatible()
    {
        Assert.Equal(6, DocumentExtractorCatalog.CreateDefault().Count);
        Assert.NotNull(new WordTextExtractor());
        Assert.NotNull(new ExcelTextExtractor());
        Assert.NotNull(new PowerPointTextExtractor());
        Assert.NotNull(new PdfTextExtractor());
        Assert.NotNull(new PdfTextExtractor(new(WordBody: false)));
        Assert.Equal(new DocumentScopeOptions(true, true, false, true, true), new());
    }

    private static async Task<List<TextSegment>> ReadUnchanged(string path, DocumentScopeOptions scope)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var hash = SHA256.HashData(bytes);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            var output = new List<TextSegment>();
            var warnings = new List<string>();
            var extractor = DocumentExtractorCatalog.CreateDefault(scope).Single(item => item.Extensions.Contains(Path.GetExtension(path)));
            await foreach (var item in extractor.ExtractAsync(path, new ExtractionContext(warnings.Add))) output.Add(item);
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            Assert.NotEmpty(warnings);
            return output;
        }
        finally { File.SetAttributes(path, attributes); }
    }

    private static W.Paragraph Paragraph(string text) => new(new W.Run(new W.Text(text)));
    private static P.Shape Shape(string name, string text) => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = name }),
        new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(text)))));
}
