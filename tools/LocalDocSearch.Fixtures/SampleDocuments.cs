using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Fixtures;

/// <summary>Creates small, original documents solely for automated tests.</summary>
/// <remarks>
/// This fixture generator is the only component intentionally writing fixture files.
/// Document search remains read-only. CreateAll writes exactly eight files inside
/// the supplied directory (which may be a test-owned temporary directory), and
/// never emits README files, companion assets, or files outside that directory.
/// Existing files with these eight names are intentionally replaced.
/// </remarks>
public static class SampleDocuments
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void CreateAll(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "中文示例.txt"),
            "入库记录：西瓜\n领用记录：苹果；字面量：.*\n", Utf8);
        File.WriteAllText(Path.Combine(root, "中文示例.md"),
            "# 原创记录：西瓜\n采购条目：苹果；字面量：.*\n", Utf8);
        File.WriteAllText(Path.Combine(root, "中文示例.log"),
            "[INFO] 入库：西瓜\n[INFO] 领用：苹果；字面量：.*\n", Utf8);
        File.WriteAllText(Path.Combine(root, "跨行字段.csv"),
            "编号,备注\r\n1,\"第一行\r\n西瓜汁，附注\"\"原样\"\",保留逗号；.*\"\r\n2,苹果\r\n", Utf8);

        CreateWord(Path.Combine(root, "正文与表格.docx"));
        CreateWorkbook(Path.Combine(root, "单元格定位.xlsx"));
        CreatePresentation(Path.Combine(root, "八张幻灯片.pptx"));
        PdfFixture.Create(Path.Combine(root, "三页文字.pdf"));
    }

    private static void CreateWord(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(
            new W.Paragraph(
                new W.Run(new W.Text("原创正文：")),
                new W.Run(new W.Text("西")),
                new W.Run(new W.Text("瓜"))),
            new W.Table(
                new W.TableProperties(new W.TableWidth { Width = "6000", Type = W.TableWidthUnitValues.Dxa }),
                new W.TableGrid(new W.GridColumn { Width = "6000" }),
                new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("苹果")))))),
            new W.Paragraph(new W.Run(new W.Text("字面量：.*")))));
        main.Document.Save();
        Validate(document);
    }

    private static void CreateWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();
        var shared = workbook.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new S.SharedStringTable(
            new S.SharedStringItem(new S.Text("西瓜"))) { Count = 1U, UniqueCount = 1U };

        var first = workbook.AddNewPart<WorksheetPart>();
        first.Worksheet = new S.Worksheet(new S.SheetData());
        var second = workbook.AddNewPart<WorksheetPart>();
        second.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(InlineCell("A1", "原创定位记录")) { RowIndex = 1U },
            new S.Row(new S.Cell { CellReference = "B15", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") }) { RowIndex = 15U },
            new S.Row(InlineCell("D20", "苹果")) { RowIndex = 20U },
            new S.Row(InlineCell("D21", ".*")) { RowIndex = 21U }));
        var hidden = workbook.AddNewPart<WorksheetPart>();
        hidden.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(InlineCell("A1", "隐藏专用词")) { RowIndex = 1U }));
        workbook.Workbook.Append(new S.Sheets(
            new S.Sheet { Name = "Sheet1", SheetId = 1U, Id = workbook.GetIdOfPart(first) },
            new S.Sheet { Name = "Sheet2", SheetId = 2U, Id = workbook.GetIdOfPart(second) },
            new S.Sheet { Name = "HiddenSheet", SheetId = 3U, Id = workbook.GetIdOfPart(hidden), State = S.SheetStateValues.Hidden }));
        shared.SharedStringTable.Save();
        first.Worksheet.Save();
        second.Worksheet.Save();
        hidden.Worksheet.Save();
        workbook.Workbook.Save();
        Validate(document);
    }

    private static S.Cell InlineCell(string reference, string text) => new()
    {
        CellReference = reference,
        DataType = S.CellValues.InlineString,
        InlineString = new S.InlineString(new S.Text(text))
    };

    private static void CreatePresentation(string path)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        var master = presentation.AddNewPart<SlideMasterPart>("rId1");
        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        layout.AddPart(master, "rId1");
        var theme = master.AddNewPart<ThemePart>("rId2");

        WriteXml(master, Root("sldMaster",
            new XElement(P + "cSld", ShapeTree()),
            ColorMap(),
            new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId",
                new XAttribute("id", 2147483649U), new XAttribute(R + "id", "rId1"))),
            new XElement(P + "txStyles", new XElement(P + "titleStyle"),
                new XElement(P + "bodyStyle"), new XElement(P + "otherStyle"))));
        WriteXml(layout, Root("sldLayout", new XAttribute("type", "blank"),
            new XAttribute("preserve", "1"),
            new XElement(P + "cSld", new XAttribute("name", "原创空白布局"), ShapeTree()),
            ColorMapOverride()));
        WriteXml(theme, CreateTheme());

        var slideIds = new XElement(P + "sldIdLst");
        for (int number = 1; number <= 8; number++)
        {
            string id = $"rId{number + 1}";
            var slide = presentation.AddNewPart<SlidePart>(id);
            slide.AddPart(layout, "rId1");
            var tree = ShapeTree();
            tree.Add(TextBox(2, "标题", 700000, 600000, $"第{number}页"));
            string[] runs = number switch
            {
                1 => ["苹果"],
                4 => ["字面量：.*"],
                8 => ["西", "瓜"],
                _ => [$"原创演示内容 {number}"]
            };
            tree.Add(TextBox(3, "正文", 700000, 1800000, runs));
            WriteXml(slide, Root("sld", new XElement(P + "cSld", tree), ColorMapOverride()));
            slideIds.Add(new XElement(P + "sldId", new XAttribute("id", (uint)(255 + number)),
                new XAttribute(R + "id", id)));
        }
        WriteXml(presentation, Root("presentation",
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId",
                new XAttribute("id", 2147483648U), new XAttribute(R + "id", "rId1"))),
            slideIds,
            new XElement(P + "sldSz", new XAttribute("cx", 12192000), new XAttribute("cy", 6858000)),
            new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000))));
        Validate(document);
    }

    private static XElement Root(string name, params object[] children) => new(P + name,
        new XAttribute(XNamespace.Xmlns + "p", P),
        new XAttribute(XNamespace.Xmlns + "a", A),
        new XAttribute(XNamespace.Xmlns + "r", R), children);

    private static XElement ShapeTree() => new(P + "spTree",
        new XElement(P + "nvGrpSpPr",
            new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")),
            new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
        new XElement(P + "grpSpPr", new XElement(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
            new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
            new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)),
            new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));

    private static XElement TextBox(int id, string name, int x, int y, params string[] runs) =>
        new(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", new XAttribute("txBox", "1")), new XElement(P + "nvPr")),
            new XElement(P + "spPr",
                new XElement(A + "xfrm",
                    new XElement(A + "off", new XAttribute("x", x), new XAttribute("y", y)),
                    new XElement(A + "ext", new XAttribute("cx", 10800000), new XAttribute("cy", 1200000))),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                new XElement(A + "noFill")),
            new XElement(P + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
                new XElement(A + "p", runs.Select(text => new XElement(A + "r",
                    new XElement(A + "rPr", new XAttribute("lang", "zh-CN"), new XAttribute("sz", 2800),
                        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "222222"))),
                        new XElement(A + "latin", new XAttribute("typeface", "Arial")),
                        new XElement(A + "ea", new XAttribute("typeface", "sans-serif"))),
                    new XElement(A + "t", text))),
                    new XElement(A + "endParaRPr", new XAttribute("lang", "zh-CN")))));

    private static XElement ColorMap() => new(P + "clrMap",
        new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"),
        new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
        Enumerable.Range(1, 6).Select(n => new XAttribute($"accent{n}", $"accent{n}")),
        new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink"));

    private static XElement ColorMapOverride() => new(P + "clrMapOvr", new XElement(A + "masterClrMapping"));

    private static XElement CreateTheme()
    {
        string[] names = ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        string[] colors = ["222222", "FFFFFF", "445566", "EEEEEE", "4472C4", "70AD47", "ED7D31", "A5A5A5", "5B9BD5", "FFC000", "0563C1", "954F72"];
        XElement Fill() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        XElement Font(string name) => new(A + name,
            new XElement(A + "latin", new XAttribute("typeface", "Arial")),
            new XElement(A + "ea", new XAttribute("typeface", "")),
            new XElement(A + "cs", new XAttribute("typeface", "")));
        return new XElement(A + "theme", new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute("name", "原创测试主题"),
            new XElement(A + "themeElements",
                new XElement(A + "clrScheme", new XAttribute("name", "测试色板"),
                    names.Select((name, index) => new XElement(A + name,
                        new XElement(A + "srgbClr", new XAttribute("val", colors[index]))))),
                new XElement(A + "fontScheme", new XAttribute("name", "测试字体"), Font("majorFont"), Font("minorFont")),
                new XElement(A + "fmtScheme", new XAttribute("name", "测试格式"),
                    new XElement(A + "fillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill())),
                    new XElement(A + "lnStyleLst", Enumerable.Range(1, 3).Select(n =>
                        new XElement(A + "ln", new XAttribute("w", n * 6350), Fill(),
                            new XElement(A + "prstDash", new XAttribute("val", "solid"))))),
                    new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ =>
                        new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                    new XElement(A + "bgFillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill())))));
    }

    private static void WriteXml(OpenXmlPart part, XElement root)
    {
        using var stream = new MemoryStream(Utf8.GetBytes(root.ToString(SaveOptions.DisableFormatting)));
        part.FeedData(stream);
    }

    private static void Validate(OpenXmlPackage document)
    {
        var errors = new OpenXmlValidator().Validate(document).Take(5).ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidDataException("Generated Open XML fixture failed validation: " +
                string.Join("; ", errors.Select(error => error.Description)));
        }
    }
}
