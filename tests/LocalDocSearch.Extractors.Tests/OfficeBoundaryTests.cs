using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using V = DocumentFormat.OpenXml.Vml;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Extractors.Tests;

public sealed class OfficeBoundaryTests
{
    [Fact]
    public async Task Word_MergesRunsButNotParagraphsOrCells_AndSkipsTextBoxesAndHeaders()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("boundaries.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var nested = new W.Table(new W.TableRow(new W.TableCell(Paragraph("嵌套苹果"))));
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("西")), new W.Run(new W.Text("瓜"))),
                Paragraph("西"), Paragraph("瓜"),
                new W.Table(new W.TableRow(new W.TableCell(Paragraph("西")), new W.TableCell(Paragraph("瓜"), nested))),
                new W.Paragraph(new W.Run(new W.Picture(new V.TextBox(new W.TextBoxContent(Paragraph("文本框禁搜"))))))));
            main.AddNewPart<HeaderPart>().Header = new W.Header(Paragraph("页眉禁搜"));
            main.AddNewPart<FooterPart>().Footer = new W.Footer(Paragraph("页脚禁搜"));
            main.AddNewPart<WordprocessingCommentsPart>().Comments = new W.Comments(new W.Comment(Paragraph("批注禁搜")) { Id = "0" });
        }
        var segments = await ExtractionTestSupport.ReadAsync(path);
        Assert.Single(segments, item => item.Text.Contains("西瓜"));
        var nestedApple = Assert.Single(segments, item => item.Text.Contains("苹果"));
        Assert.Equal(SourceKind.WordTableCell, nestedApple.Source.Kind);
        Assert.Equal(2, nestedApple.Source.Table);
        Assert.Equal(1, nestedApple.Source.Row);
        Assert.Equal(1, nestedApple.Source.Cell);
        Assert.Equal(1, nestedApple.Source.Paragraph);
        Assert.DoesNotContain(segments, item => item.Text.Contains("禁搜"));
        Assert.Equal(segments.Count, segments.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task Excel_UsesA1CoordinatesAndRichStrings_AndSkipsHiddenSheets()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("coordinates.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = Workbook(document);
            workbook.AddNewPart<SharedStringTablePart>().SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Run(new S.Text("西")), new S.Run(new S.Text("瓜")), new S.PhoneticRun(new S.Text("拼音禁搜"))));
            AddSheet(workbook, "Sheet2", new S.SheetData(new S.Row(
                new S.Cell { CellReference = "B15", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                Inline("D20", "苹", "果"), Inline("F99", "西"), Inline("G99", "瓜")) { RowIndex = 99U }));
            AddSheet(workbook, "Hidden", new S.SheetData(new S.Row(Inline("A1", "隐藏禁搜"))), S.SheetStateValues.Hidden);
            AddSheet(workbook, "VeryHidden", new S.SheetData(new S.Row(Inline("A1", "深藏禁搜"))), S.SheetStateValues.VeryHidden);
        }
        var segments = await ExtractionTestSupport.ReadAsync(path);
        var melon = Assert.Single(segments, item => item.Text.Contains("西瓜"));
        Assert.Equal("Sheet2", melon.Source.Sheet);
        Assert.Equal("B15", melon.Source.Address);
        Assert.Equal(15, melon.Source.Row);
        Assert.Equal(2, melon.Source.Cell);
        Assert.Contains(segments, item => item.Text == "苹果" && item.Source.Address == "D20");
        Assert.DoesNotContain(segments, item => item.Text.Contains("禁搜"));
        Assert.DoesNotContain(segments, item => item.Text == "西瓜" && item.Source.Address != "B15");
    }

    [Fact]
    public async Task Excel_UncachedFormulaWarnsAndDoesNotSearchFormulaSource()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("formula.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = Workbook(document);
            AddSheet(workbook, "Sheet1", new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", CellFormula = new S.CellFormula("\"西瓜\"") },
                new S.Cell { CellReference = "B1", DataType = S.CellValues.String, CellFormula = new S.CellFormula("\"源码禁搜\""), CellValue = new S.CellValue("苹果") },
                new S.Cell { CellReference = "C1", CellFormula = new S.CellFormula("1+1"), CellValue = new S.CellValue() },
                new S.Cell { CellReference = "D1", DataType = S.CellValues.String, CellFormula = new S.CellFormula("\"\""), CellValue = new S.CellValue("") })));
        }
        var warnings = new List<string>();
        var segments = await ExtractionTestSupport.ReadAsync(path, warnings);
        var cached = Assert.Single(segments);
        Assert.Equal("苹果", cached.Text);
        Assert.Equal("公式缓存", cached.Source.ValueKind);
        Assert.Contains(warnings, message => message.Contains("Sheet1!A1") && message.Contains("缓存"));
        Assert.Contains(warnings, message => message.Contains("Sheet1!C1") && message.Contains("缓存"));
        Assert.DoesNotContain(warnings, message => message.Contains("Sheet1!D1"));
        Assert.DoesNotContain(segments, item => item.Text.Contains("西瓜") || item.Text.Contains("源码"));
    }

    [Fact]
    public async Task Excel_PreservesStoredScalarValuesWithoutPretendingToFormatThem()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("raw.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = Workbook(document);
            AddSheet(workbook, "Values", new S.SheetData(new S.Row(
                Scalar("A1", S.CellValues.Boolean, "1"), Scalar("B1", S.CellValues.Number, "45123.5"),
                Scalar("C1", S.CellValues.Error, "#DIV/0!"), Scalar("D1", S.CellValues.Date, "2026-11-09T12:00:00Z"),
                Scalar("E1", S.CellValues.String, "原始字符串"))));
        }
        var warnings = new List<string>();
        var segments = await ExtractionTestSupport.ReadAsync(path, warnings);
        Assert.Equal(new[] { "1", "45123.5", "#DIV/0!", "2026-11-09T12:00:00Z", "原始字符串" }, segments.Select(item => item.Text));
        Assert.Equal(new[] { "布尔原始值", "数字原始值", "错误原始值", "日期原始值", "字符串原始值" }, segments.Select(item => item.Source.ValueKind));
        Assert.Single(warnings, message => message.Contains("原始值") && message.Contains("不重算"));
    }

    [Fact]
    public async Task Excel_MissingCoordinatesWarnInsteadOfInventingLocations()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("unlocated.xlsx");
        using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var workbook = Workbook(document);
            AddSheet(workbook, "Values", new S.SheetData(new S.Row(
                new S.Cell { DataType = S.CellValues.String, CellValue = new S.CellValue("西瓜") },
                Scalar("XFE1", S.CellValues.String, "苹果"))));
        }
        var warnings = new List<string>();
        Assert.Empty(await ExtractionTestSupport.ReadAsync(path, warnings));
        Assert.Equal(2, warnings.Count(message => message.Contains("未猜测位置")));
    }

    [Fact]
    public async Task PowerPoint_UsesRelationshipOrderAndObjectNames_WithoutDuplicateTableOrGroupText()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("slide-order.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var storedFirst = presentation.AddNewPart<SlidePart>();
            storedFirst.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(Shape("末页文本", "西", "瓜"))));
            var table = new A.Table(new A.TableProperties(), new A.TableGrid(new A.GridColumn { Width = 1L }, new A.GridColumn { Width = 1L }),
                new A.TableRow(TableCell("苹", "果"), TableCell("西")) { Height = 1L },
                new A.TableRow(TableCell("瓜"), TableCell("表格次段")) { Height = 1L });
            var frame = new P.GraphicFrame(
                new P.NonVisualGraphicFrameProperties(new P.NonVisualDrawingProperties { Id = 9U, Name = "目标表格" }, new P.NonVisualGraphicFrameDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                new P.Transform(), new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
            var storedSecond = presentation.AddNewPart<SlidePart>();
            storedSecond.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(frame, new P.GroupShape(Shape("组合对象", "组合禁搜")))));
            storedSecond.AddNewPart<NotesSlidePart>().NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(Shape("备注对象", "备注禁搜"))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(storedSecond) },
                new P.SlideId { Id = 257U, RelationshipId = presentation.GetIdOfPart(storedFirst) });
        }
        var segments = await ExtractionTestSupport.ReadAsync(path);
        var apple = Assert.Single(segments, item => item.Text.Contains("苹果"));
        Assert.Equal(SourceKind.SlideTableCell, apple.Source.Kind);
        Assert.Equal(1, apple.Source.Slide);
        Assert.Equal("目标表格", apple.Source.ObjectName);
        Assert.Equal(1, apple.Source.Row);
        Assert.Equal(1, apple.Source.Cell);
        var melon = Assert.Single(segments, item => item.Text.Contains("西瓜"));
        Assert.Equal(2, melon.Source.Slide);
        Assert.Equal("末页文本", melon.Source.ObjectName);
        Assert.DoesNotContain(segments, item => item.Text.Contains("禁搜"));
        Assert.Equal(segments.Count, segments.Select(item => item.Id).Distinct().Count());
    }

    private static W.Paragraph Paragraph(string text) => new(new W.Run(new W.Text(text)));
    private static S.Cell Inline(string address, params string[] runs) => new()
    {
        CellReference = address, DataType = S.CellValues.InlineString,
        InlineString = new S.InlineString(runs.Select(text => new S.Run(new S.Text(text))))
    };
    private static S.Cell Scalar(string address, S.CellValues type, string value) => new()
    {
        CellReference = address, DataType = type, CellValue = new S.CellValue(value)
    };
    private static WorkbookPart Workbook(SpreadsheetDocument document)
    {
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook(new S.Sheets());
        return workbook;
    }
    private static void AddSheet(WorkbookPart workbook, string name, S.SheetData data, S.SheetStateValues? state = null)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        part.Worksheet = new S.Worksheet(data);
        var sheets = workbook.Workbook?.Sheets ?? throw new InvalidOperationException("Test workbook has no sheet catalog.");
        var sheet = new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = (uint)sheets.ChildElements.Count + 1, Name = name };
        if (state.HasValue) sheet.State = state.Value;
        sheets.Append(sheet);
    }
    private static P.Shape Shape(string name, params string[] runs) => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 2U, Name = name }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(), new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(runs.Select(text => new A.Run(new A.Text(text))))));
    private static A.TableCell TableCell(params string[] runs) => new(
        new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(runs.Select(text => new A.Run(new A.Text(text))))), new A.TableCellProperties());
}
