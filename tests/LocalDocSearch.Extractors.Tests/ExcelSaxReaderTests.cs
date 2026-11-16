using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LocalDocSearch.Extractors.Tests;

public sealed class ExcelSaxReaderTests
{
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [Fact]
    public async Task RichTextSharedInlineAndScalarValues_MatchOriginalDomIncludingAdjacentEmptyItems()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("rich.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row r=\"999\">" +
            "<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c>" +
            "<c/><c r=\"D1\" t=\"inlineStr\"><is><t>头</t><r><t>内</t><t>联</t></r><rPh sb=\"0\" eb=\"1\"><t>忽略</t></rPh><t>尾</t></is></c>" +
            "<c r=\"E1\" t=\"b\"><v>1</v></c><c r=\"F1\" t=\"e\"><v>#DIV/0!</v></c>" +
            "<c r=\"G1\" t=\"d\"><v>2026-11-15</v></c><c r=\"H1\"><v>45123.50</v></c>" +
            "<c r=\"I1\" t=\"str\"><f>not-searched</f><v>缓存</v></c>" +
            "<c r=\"J1\"><f>missing</f></c><c r=\"K1\"><f>empty</f><v/></c>" +
            "<c r=\"L1\" t=\"str\"><f>empty-string</f><v/></c>" +
            "<c r=\"M1\" t=\"inlineStr\"><is/></c><c r=\"N1\" t=\"str\"><v>最后</v></c>" +
            "</row></sheetData>")],
            Shared("<si><t>首</t><r><t>共</t><t>享</t></r><rPh sb=\"0\" eb=\"1\"><t>忽略</t></rPh><t>尾</t></si><si/><si><t>相邻</t></si>"));

        var actual = await AssertEquivalent(path);
        Assert.Null(actual.Error);
        Assert.Equal(new[] { "首共享尾", "相邻", "头内联尾", "1", "#DIV/0!", "2026-11-15", "45123.50", "缓存", "最后" },
            actual.Segments.Select(segment => segment.Text));
        Assert.Equal(9, actual.Segments.Count);
        Assert.Equal(4, actual.Warnings.Count);
    }

    public static IEnumerable<object[]> AddressCases()
    {
        yield return ["<c t=\"s\"><v>999</v></c><c r=\"A0\"/><c r=\"XFE1\"/><c r=\"A1048577\"/><c r=\"A01\"/><c r=\"a1\" t=\"str\"><v>保留</v></c>", false];
        yield return ["<c r=\"A1\"/><c r=\"a1\" t=\"str\"><v>重复空白</v></c>", true];
        yield return ["<c r=\"A1\" t=\"str\"><v> </v></c><c r=\"A1\"/>", true];
        yield return ["<c r=\"A1\"><f>没有缓存</f></c><c r=\"A1\"/>", true];
        yield return ["<c r=\"A1\" t=\"str\"><v>首</v></c><c r=\"A1&#10;\" t=\"str\"><v>异</v></c>", false];
        yield return ["<c r=\"A1&#10;\"/><c r=\"a1&#10;\"/>", true];
        yield return ["<c r=\" A1\"/><c r=\"A1 \"/><c r=\"A1&#13;&#10;\"/><c r=\"XFD1048576\" t=\"str\"><v>界</v></c>", false];
        yield return ["<c r=\"A1\" t=\"s\"><v>-1</v></c>", true];
        yield return ["<c r=\"A1\" t=\"s\"><v> 0</v></c>", true];
        yield return ["<c r=\"A1\" t=\"s\"><v>999</v></c>", true];
        yield return ["<c r=\"A1\" t=\"not-a-cell-type\"><v>x</v></c>", true];
    }

    [Theory]
    [MemberData(nameof(AddressCases))]
    public async Task AddressesEmptyCellsDuplicatesAndInvalidIndexes_MatchOriginalDom(string cells, bool fails)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("addresses.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row>" + cells + "</row></sheetData>")], Shared("<si><t>共享</t></si>"));
        var actual = await AssertEquivalent(path);
        Assert.Equal(fails, actual.Error is not null);
        if (fails) Assert.Empty(actual.Segments);
    }

    public static IEnumerable<object[]> ShapeCases()
    {
        yield return ["<sheetData/><sheetData><row><c r=\"A1\" t=\"str\"><v>第二块</v></c></row></sheetData>", 0];
        yield return ["<sheetData/><extLst><sheetData><row><c r=\"A1\" t=\"str\"><v>嵌套块</v></c></row></sheetData></extLst><sheetData><row><c r=\"B1\" t=\"str\"><v>后续块</v></c></row></sheetData>", 0];
        yield return ["<sheetData><row/><extLst><c r=\"A1\" t=\"str\"><v>未知容器</v></c></extLst><row/></sheetData><extLst><sheetData><row><c r=\"B1\" t=\"str\"><v>嵌套块</v></c></row></sheetData></extLst><sheetData><row><c r=\"C1\" t=\"str\"><v>后续块</v></c></row></sheetData>", 0];
        yield return ["<sheetData><row/><extLst><c r=\"A1\" t=\"str\"><v>未知容器</v></c></extLst><row><c r=\"B1\" t=\"str\"><v>保留</v></c></row><row/></sheetData><extLst><row><c r=\"C1\" t=\"str\"><v>外部未知容器</v></c></row></extLst><sheetData><row><c r=\"D1\" t=\"str\"><v>后续块</v></c></row></sheetData>", 1];
        yield return ["<extLst><sheetData><row><c r=\"A1\" t=\"str\"><v>嵌套</v></c></row></sheetData></extLst>", 0];
        yield return ["<sheetData><extLst><row><c r=\"A1\" t=\"str\"><v>嵌套行</v></c></row></extLst><row><extLst><c r=\"B1\" t=\"str\"><v>嵌套格</v></c></extLst><c r=\"C1\" t=\"str\"><v>直属</v></c></row></sheetData>", 1];
        yield return ["<sheetData><row><c r=\"A1\" t=\"str\"><v>首</v></c><row><c r=\"A1\" t=\"str\"><v>嵌套行</v></c></row><c r=\"B1\" t=\"str\"><v>尾</v></c></row></sheetData><sheetData><row><c r=\"A1\"/></row></sheetData>", 2];
        yield return ["<sheetData><row/><row><c/><c r=\"A1\" t=\"str\"><v>一</v></c></row><row/><row><c r=\"B2\" t=\"str\"><v>二</v></c></row></sheetData>", 2];
        yield return ["<sheetData><c r=\"A1\" t=\"str\"><v>无行</v></c><row><c r=\"B1\" t=\"str\"><v>在行</v></c></row></sheetData>", 1];
        yield return ["<x:sheetData xmlns:x=\"urn:unknown\"><x:row><x:c r=\"A1\"/></x:row></x:sheetData><sheetData><row><c r=\"A1\" t=\"str\"><v>普通</v></c></row></sheetData>", 1];
    }

    [Theory]
    [MemberData(nameof(ShapeCases))]
    public async Task OnlyFirstDirectSheetDataAndDirectRowsAndCells_AreTraversed(string body, int count)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("shape.xlsx");
        CreateWorkbook(path, [Worksheet(body)]);
        var actual = await AssertEquivalent(path);
        Assert.Null(actual.Error);
        Assert.Equal(count, actual.Segments.Count);
    }

    [Fact]
    public async Task SharedStringLookup_UsesOnlyDirectItemsAndDirectTextAndRuns()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("shared-shape.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row></sheetData>")],
            Shared("<extLst><si><t>嵌套项</t></si></extLst><si><extLst><t>嵌套文本</t></extLst><r><r><t>嵌套run</t></r><t>直属run</t></r></si><si><t>下一项</t></si>"));
        var actual = await AssertEquivalent(path);
        Assert.Null(actual.Error);
        Assert.Equal(new[] { "直属run", "下一项" }, actual.Segments.Select(segment => segment.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedItemCountLimit_StillFollowsCompletePartValidation(bool malformedTail)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("shared-count.xlsx");
        var items = string.Concat(Enumerable.Repeat("<si/>", BoundedSegmentBuffer.DefaultMaxSegments + 1));
        var xml = Shared(items + (malformedTail ? "<broken>" : ""));
        CreateWorkbook(path, [Worksheet("<sheetData/>")], xml);
        var actual = await AssertEquivalent(path);
        Assert.NotNull(actual.Error);
        Assert.Empty(actual.Segments);
        Assert.Single(actual.Warnings);
        Assert.Contains(malformedTail ? "System.Xml.XmlException" : "共享字符串数量超过上限", actual.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("abc")]
    [InlineData("<!--only a comment-->")]
    [InlineData("<?xml version=\"1.0\"?><!--comment-->")]
    [InlineData("<unexpected/>")]
    [InlineData("<worksheet/>")]
    public async Task EmptyMissingAndWrongRoots_MatchOriginalDom(string xml)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("root.xlsx");
        CreateWorkbook(path, [xml]);
        await AssertEquivalent(path);
        var sharedPath = directory.FileFor("shared-root.xlsx");
        CreateWorkbook(sharedPath, [Worksheet("<sheetData/>")], xml);
        await AssertEquivalent(sharedPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeHiddenSheetNumberingAndRelationshipOrder_ArePreserved(bool includeHidden)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("scope.xlsx");
        CreateWorkbook(path,
            [Worksheet("<sheetData><row><c r=\"A1\" t=\"str\"><v>隐藏</v></c></row></sheetData>"),
             Worksheet("<sheetData><row><c r=\"B2\" t=\"str\"><v>可见</v></c></row></sheetData>"),
             Worksheet("<sheetData><row><c r=\"C3\" t=\"str\"><v>深藏</v></c></row></sheetData>")],
            states: ["hidden", "visible", "veryHidden"]);
        var actual = await AssertEquivalent(path, new(ExcelHiddenSheets: includeHidden));
        Assert.Null(actual.Error);
        Assert.Contains(actual.Segments, segment => segment.Id == "excel:s2:B2" && segment.Source.Sheet == "Sheet2");
        Assert.Equal(includeHidden ? 3 : 1, actual.Segments.Count);
    }

    [Fact]
    public async Task MalformedHiddenSheet_IsNotOpenedUnlessIncluded()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("hidden-malformed.xlsx");
        CreateWorkbook(path, ["<worksheet>", Worksheet("<sheetData><row><c r=\"A1\" t=\"str\"><v>可见</v></c></row></sheetData>")],
            states: ["hidden", "visible"]);
        Assert.Null((await AssertEquivalent(path)).Error);
        Assert.NotNull((await AssertEquivalent(path, new(ExcelHiddenSheets: true))).Error);
    }

    [Theory]
    [InlineData("worksheet")]
    [InlineData("shared")]
    [InlineData("later-sheet")]
    public async Task LateMalformedXml_WinsOverEarlierBusinessErrorsWithoutPublishing(string part)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("late-malformed.xlsx");
        var early = Worksheet("<sheetData><row><c/><c r=\"A1\" t=\"s\"><v>999</v></c></row></sheetData>");
        CreateWorkbook(path,
            part == "later-sheet" ? [Worksheet("<sheetData><row><c r=\"A1\" t=\"str\"><v>先前表</v></c></row></sheetData>"), early[..^12] + "<broken></worksheet>"] :
                [part == "worksheet" ? early[..^12] + "<broken></worksheet>" : early],
            part == "shared" ? Shared("<si><t>ok</t></si>")[..^6] + "<broken></sst>" : Shared("<si><t>ok</t></si>"));
        var actual = await AssertEquivalent(path);
        Assert.NotNull(actual.Error);
        Assert.Empty(actual.Segments);
        Assert.Single(actual.Warnings); // No invalid-coordinate warning from an unvalidated part.
        Assert.Contains("System.Xml.XmlException", actual.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PartCharacterBudget_IncludesUnusedCommentXmlBeforeCellWarnings(bool sharedPart, bool exceed)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("part-budget.xlsx");
        var worksheet = Worksheet("<sheetData><row><c/><c r=\"A1\" t=\"s\"><v>0</v></c></row></sheetData>");
        var shared = Shared("<si><t>ok</t></si>");
        var xml = sharedPart ? shared : worksheet;
        var end = sharedPart ? "</sst>" : "</worksheet>";
        var padding = checked((int)ReadOnlyExtraction.MaxPartCharacters - xml.Length - 7 + (exceed ? 1 : 0));
        xml = xml[..^end.Length] + "<!--" + new string('x', padding) + "-->" + end;
        Assert.Equal(ReadOnlyExtraction.MaxPartCharacters + (exceed ? 1 : 0), xml.Length);
        CreateWorkbook(path, [sharedPart ? worksheet : xml], sharedPart ? xml : shared);
        var actual = await AssertEquivalent(path);
        Assert.Equal(exceed, actual.Error is not null);
        if (exceed)
        {
            Assert.Empty(actual.Segments);
            Assert.Single(actual.Warnings);
            Assert.Contains("MaxCharactersInDocument", actual.Error);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationAtWarningBoundariesAndBeforeOpen_PreservesAtomicity(int warningBoundary)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("cancel.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row><c/><c r=\"A1\"><f>uncached</f></c><c r=\"B1\" t=\"str\"><v>ok</v></c></row></sheetData>")]);
        var actual = await AssertEquivalent(path, cancelAfterWarning: warningBoundary, preCancelled: warningBoundary == 0);
        Assert.Contains("OperationCanceledException", actual.Error);
        Assert.Empty(actual.Segments);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task SegmentLimitsAndUnusedSharedStrings_MatchOriginalDom(int limit)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("segment-budget.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row><c r=\"A1\" t=\"str\"><v>ok</v></c><c r=\"B1\" t=\"inlineStr\"><is><r><t>app</t></r><r><t>le</t></r></is></c></row></sheetData>")],
            Shared("<si><t>unused lookup exceeds either result limit</t></si>"));
        var actual = await AssertEquivalent(path, limits: new SearchResourceLimits { MaxSegmentCharacters = limit });
        Assert.Equal(limit < 5, actual.Error is not null);
        if (limit < 5) Assert.Empty(actual.Segments);
    }

    [Fact]
    public async Task CustomCurrentCultureCoordinateParsing_RemainsTheOriginalContract()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("culture.xlsx");
        CreateWorkbook(path, [Worksheet("<sheetData><row><c r=\"A19\" t=\"str\"><v>positive</v></c><c r=\"A29\" t=\"str\"><v>negative</v></c><c r=\"A1\" t=\"str\"><v>skip</v></c></row></sheetData>")]);
        var previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "1";
        culture.NumberFormat.NegativeSign = "2";
        try
        {
            CultureInfo.CurrentCulture = culture;
            var actual = await AssertEquivalent(path);
            Assert.Null(actual.Error);
            Assert.Equal(new int?[] { 9, -9 }, actual.Segments.Select(segment => segment.Source.Row));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private sealed record Outcome(List<TextSegment> Segments, List<string> Warnings, string? Error);

    private static async Task<Outcome> AssertEquivalent(string path, DocumentScopeOptions? options = null,
        SearchResourceLimits? limits = null, int cancelAfterWarning = -1, bool preCancelled = false)
    {
        var original = await Capture(path, true, options, limits, cancelAfterWarning, preCancelled);
        var actual = await Capture(path, false, options, limits, cancelAfterWarning, preCancelled);
        Assert.Equal(original.Segments, actual.Segments);
        Assert.Equal(original.Warnings, actual.Warnings);
        Assert.Equal(original.Error, actual.Error);
        // Neither reader may keep its ZIP or file stream open after enumeration.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return actual;
    }

    private static async Task<Outcome> Capture(string path, bool original, DocumentScopeOptions? options,
        SearchResourceLimits? limits, int cancelAfterWarning, bool preCancelled)
    {
        var output = new List<TextSegment>();
        var warnings = new List<string>();
        using var cancellation = new CancellationTokenSource();
        if (preCancelled) cancellation.Cancel();
        var context = new ExtractionContext(message =>
        {
            warnings.Add(message);
            if (warnings.Count == cancelAfterWarning) cancellation.Cancel();
        }, limits: limits);
        try
        {
            var segments = original ? new OriginalDomExtractor(options).ExtractAsync(path, context, cancellation.Token) :
                new ExcelTextExtractor(options).ExtractAsync(path, context, cancellation.Token);
            await foreach (var segment in segments) output.Add(segment);
            return new(output, warnings, null);
        }
        catch (Exception error)
        {
            var errors = new List<string>();
            for (Exception? current = error; current is not null; current = current.InnerException)
                errors.Add(current.GetType().FullName + ": " + current.Message);
            return new(output, warnings, string.Join("\n", errors));
        }
    }

    private static string Worksheet(string body) => $"<worksheet xmlns=\"{SheetNamespace}\">{body}</worksheet>";
    private static string Shared(string body) => $"<sst xmlns=\"{SheetNamespace}\">{body}</sst>";

    private static void CreateWorkbook(string path, string[] worksheets, string? shared = null, string[]? states = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WritePart("_rels/.rels", Relationships([( "rId1", "officeDocument", "xl/workbook.xml" )]));
        WritePart("xl/workbook.xml", $"<workbook xmlns=\"{SheetNamespace}\" xmlns:r=\"{RelationNamespace}\"><sheets>" +
            string.Concat(worksheets.Select((_, index) => $"<sheet name=\"Sheet{index + 1}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\" state=\"{states?[index] ?? "visible"}\"/>")) + "</sheets></workbook>");
        var relations = worksheets.Select((_, index) => ($"rId{index + 1}", "worksheet", $"worksheets/sheet{index + 1}.xml")).ToList();
        if (shared is not null) relations.Add(("rIdStrings", "sharedStrings", "sharedStrings.xml"));
        WritePart("xl/_rels/workbook.xml.rels", Relationships(relations));
        for (var index = 0; index < worksheets.Length; index++) WritePart($"xl/worksheets/sheet{index + 1}.xml", worksheets[index]);
        if (shared is not null) WritePart("xl/sharedStrings.xml", shared);
        WritePart("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            string.Concat(worksheets.Select((_, index) => $"<Override PartName=\"/xl/worksheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
            (shared is null ? "" : "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>") + "</Types>");
        return;

        void WritePart(string name, string xml)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    private static string Relationships(IEnumerable<(string Id, string Type, string Target)> relations) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(relations.Select(item => $"<Relationship Id=\"{item.Id}\" Type=\"{RelationNamespace}/{item.Type}\" Target=\"{item.Target}\"/>")) + "</Relationships>";

    // Independent snapshot of the pre-SAX DOM algorithm. Keep it here rather than
    // sharing production traversal/coordinate helpers: that would hide regressions.
    private sealed class OriginalDomExtractor(DocumentScopeOptions? options)
    {
        private readonly DocumentScopeOptions _options = options ?? new();
        private static readonly Regex AddressPattern = new("^([A-Za-z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken token) =>
            ReadOnlyExtraction.Run(path, context, Read, token);

        private List<TextSegment> Read(FileStream stream, ExtractionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OpenXmlPackageBudget.Validate(stream, token);
            using var document = SpreadsheetDocument.Open(stream, false, ReadOnlyExtraction.Settings());
            token.ThrowIfCancellationRequested();
            var workbook = document.WorkbookPart ?? throw new ExtractionException("XLSX 缺少工作簿。");
            var sheets = workbook.Workbook?.Sheets ?? throw new ExtractionException("XLSX 缺少工作表目录。");
            context.Warn(_options.ExcelHiddenSheets ? "Excel 搜索普通工作表（含隐藏和深度隐藏）；值为文件保存的原始值或公式缓存，不重算公式，也不代表 Excel 格式化显示值；批注、图表等不搜索。" : "Excel 仅搜索可见普通工作表；值为文件保存的原始值或公式缓存，不重算公式，也不代表 Excel 格式化显示值；批注、图表等不搜索。");
            var sharedStrings = new List<string>();
            long sharedCharacters = 0;
            if (workbook.SharedStringTablePart?.SharedStringTable is { } shared)
                foreach (var item in shared.Elements<S.SharedStringItem>())
                {
                    token.ThrowIfCancellationRequested();
                    if (sharedStrings.Count >= BoundedSegmentBuffer.DefaultMaxSegments)
                        throw new ExtractionException($"Excel 共享字符串数量超过上限（{BoundedSegmentBuffer.DefaultMaxSegments:N0}）；未发布部分文档。");
                    var value = RichText(item, int.MaxValue, BoundedSegmentBuffer.DefaultMaxCharacters - sharedCharacters, token);
                    sharedCharacters += value.Length;
                    sharedStrings.Add(value);
                }
            var output = new BoundedSegmentBuffer(maxSegmentCharacters: context.Limits.MaxSegmentCharacters);
            var sheetIndex = 0;
            foreach (var sheet in sheets.Elements<S.Sheet>())
            {
                token.ThrowIfCancellationRequested();
                sheetIndex++;
                if (!_options.ExcelHiddenSheets && sheet.State is { } state && state.Value != S.SheetStateValues.Visible) continue;
                var name = sheet.Name?.Value;
                var relation = sheet.Id?.Value;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(relation))
                    throw new ExtractionException("XLSX 工作表缺少名称或关系标识。");
                if (workbook.GetPartById(relation) is not WorksheetPart worksheet) continue;
                var data = worksheet.Worksheet?.GetFirstChild<S.SheetData>();
                token.ThrowIfCancellationRequested();
                if (data is null) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in data.Elements<S.Row>())
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var cell in row.Elements<S.Cell>())
                    {
                        token.ThrowIfCancellationRequested();
                        var address = cell.CellReference?.Value;
                        if (!TryCoordinates(address, out var rowNumber, out var column))
                        {
                            context.Warn($"工作表 {name} 中有缺少有效 A1 坐标的单元格，已跳过，未猜测位置。");
                            continue;
                        }
                        address = address!.ToUpperInvariant();
                        if (!seen.Add(address)) throw new ExtractionException($"工作表 {name} 存在重复单元格坐标 {address}。");
                        var formula = cell.CellFormula is not null;
                        var type = cell.DataType?.Value ?? S.CellValues.Number;
                        var raw = cell.CellValue?.Text;
                        if (formula && (cell.CellValue is null || (string.IsNullOrEmpty(raw) && type != S.CellValues.String)))
                        {
                            context.Warn($"{name}!{address} 的公式没有保存缓存值，已跳过；未搜索公式源码。");
                            continue;
                        }
                        string? value;
                        string kind;
                        if (type == S.CellValues.SharedString)
                        {
                            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= sharedStrings.Count)
                                throw new ExtractionException($"{name}!{address} 的共享字符串索引无效。");
                            value = sharedStrings[index];
                            kind = "共享字符串";
                        }
                        else if (type == S.CellValues.InlineString)
                        {
                            value = cell.InlineString is { } inline
                                ? RichText(inline, context.Limits.MaxSegmentCharacters, output.RemainingCharacters, token) : null;
                            kind = "内联字符串";
                        }
                        else
                        {
                            value = raw;
                            kind = type == S.CellValues.Boolean ? "布尔原始值" :
                                type == S.CellValues.Error ? "错误原始值" :
                                type == S.CellValues.Date ? "日期原始值" :
                                type == S.CellValues.String ? "字符串原始值" : "数字原始值";
                        }
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        token.ThrowIfCancellationRequested();
                        output.Add(new TextSegment($"excel:s{sheetIndex}:{address}", value,
                            new SourceLocator(SourceKind.ExcelCell, Sheet: name, Address: address, Row: rowNumber, Cell: column,
                                ValueKind: formula ? "公式缓存" : kind)));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return output.Complete();
        }

        private static string RichText(OpenXmlElement parent, int maxSegmentCharacters, long remainingCharacters, CancellationToken token)
        {
            var result = new BoundedTextBuilder(maxSegmentCharacters, remainingCharacters);
            foreach (var child in parent.ChildElements)
            {
                token.ThrowIfCancellationRequested();
                if (child is S.Text text) result.Append(text.Text);
                else if (child is S.Run run)
                    foreach (var textInRun in run.Elements<S.Text>())
                    {
                        token.ThrowIfCancellationRequested();
                        result.Append(textInRun.Text);
                    }
            }
            return result.ToString();
        }

        private static bool TryCoordinates(string? address, out int row, out int column)
        {
            row = column = 0;
            if (address is null) return false;
            var match = AddressPattern.Match(address);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out row) || row > 1048576) return false;
            foreach (var letter in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + letter - 'A' + 1;
            return column <= 16384;
        }
    }
}
