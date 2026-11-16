using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Extractors.Tests;

public sealed class OfficeSaxReaderTests
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Warning = "Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。";
    private const string Start = "<w:document xmlns:w=\"" + WordNamespace + "\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:x=\"urn:office-sax-test\"><w:body>";
    private const string End = "</w:body></w:document>";

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Word_MatchesFrozenDomAcrossWrappersNestedTablesAndSkippedTrees(bool body, bool tables)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("word-sax.docx");
        var skips = string.Concat(new[] { "drawing", "pict", "object", "txbxContent", "del", "moveFrom" }
            .Select(name => $"<w:{name}>{Paragraph("跳过树禁搜")}{Table(Paragraph("跳过表禁搜"))}</w:{name}>")) +
            "<mc:AlternateContent><mc:Choice Requires=\"w\">" + Paragraph("兼容选择禁搜") + "</mc:Choice><mc:Fallback>" +
            Paragraph("兼容回退禁搜") + "</mc:Fallback></mc:AlternateContent>";
        var rich = "<w:p><w:pPr/><w:r><w:rPr><w:b/></w:rPr><w:t>正文</w:t><w:tab/><w:t>🍉</w:t><w:br/><w:t>换行</w:t><w:cr/></w:r>" +
            "<w:hyperlink><w:r><w:t>链接</w:t></w:r></w:hyperlink><w:ins><w:r><w:t>插入</w:t></w:r></w:ins>" +
            "<w:del><w:r><w:delText>删除禁搜</w:delText><w:t>删除禁搜</w:t></w:r></w:del>" +
            "<w:r><w:drawing>" + Paragraph("段内图形禁搜") + "</w:drawing></w:r></w:p>";
        var nested = Table(Paragraph("内表"));
        var firstCell = "<w:tc>" + Paragraph("  \t ") + Paragraph("外表前") + skips + nested + Paragraph("外表后") +
            "<w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr></w:tc>";
        var firstRow = "<w:tr>" + skips + "<w:sdt><w:sdtContent>" + firstCell + "</w:sdtContent></w:sdt>" +
            "<w:customXml w:element=\"cell\"><w:tc>" + Paragraph("另一格") + "</w:tc></w:customXml>" +
            // Late properties deliberately exercise the original DOM getters, not schema ordering assumptions.
            "<w:trPr><w:gridBefore w:val=\"2\"/></w:trPr></w:tr>";
        var secondRow = "<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"0\"/></w:tcPr>" + Paragraph("零跨度") +
            "</w:tc><w:tc>" + Paragraph("后一格") + "</w:tc></w:tr>";
        var wrappedTable = "<w:tbl>" + skips + "<w:sdt><w:sdtContent>" + firstRow + "</w:sdtContent></w:sdt>" + secondRow + "</w:tbl>";
        CreateWord(path, Start + "<w:sdt><w:sdtContent>" + rich + Paragraph("  ") +
            "<w:customXml w:element=\"body\">" + Paragraph("wrapper正文") + "</w:customXml>" + skips + wrappedTable +
            Table(Paragraph("尾表")) + Paragraph("尾正文") + "</w:sdtContent></w:sdt>" + End);
        var bytes = await File.ReadAllBytesAsync(path);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            var scope = new DocumentScopeOptions(WordBody: body, WordTables: tables);
            var expected = await Capture(context => Legacy(path, context, scope));
            var actual = await Capture(context => new WordTextExtractor(scope).ExtractAsync(path, context));
            AssertEquivalent(expected, actual);
            Assert.Null(actual.Error);
            Assert.Equal(new[] { Warning }, actual.Warnings);
            Assert.DoesNotContain(actual.Segments, segment => segment.Text.Contains("禁搜", StringComparison.Ordinal));
            if (body)
            {
                Assert.Equal("正文\t🍉\n换行\n链接插入", Assert.Single(actual.Segments, item => item.Id == "word:p1").Text);
                Assert.Equal("word:p3", Assert.Single(actual.Segments, item => item.Text == "wrapper正文").Id);
                Assert.Equal("word:p4", Assert.Single(actual.Segments, item => item.Text == "尾正文").Id);
            }
            if (tables)
            {
                Assert.Equal("word:t1:r1:c3:p2", Assert.Single(actual.Segments, item => item.Text == "外表前").Id);
                Assert.Equal("word:t2:r1:c1:p1", Assert.Single(actual.Segments, item => item.Text == "内表").Id);
                Assert.Equal("word:t1:r1:c3:p3", Assert.Single(actual.Segments, item => item.Text == "外表后").Id);
                Assert.Equal("word:t1:r1:c5:p1", Assert.Single(actual.Segments, item => item.Text == "另一格").Id);
                Assert.Equal("word:t1:r2:c2:p1", Assert.Single(actual.Segments, item => item.Text == "后一格").Id);
                Assert.Equal("word:t3:r1:c1:p1", Assert.Single(actual.Segments, item => item.Text == "尾表").Id);
            }
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            using var released = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { File.SetAttributes(path, attributes); }
    }

    [Theory]
    [InlineData("late-close", true)]
    [InlineData("late-close", false)]
    [InlineData("skipped-corruption", true)]
    [InlineData("skipped-corruption", false)]
    [InlineData("doctype", true)]
    [InlineData("part-limit", true)]
    [InlineData("wrong-root", true)]
    [InlineData("wrong-root-malformed", true)]
    [InlineData("empty-part", true)]
    [InlineData("short-part", true)]
    [InlineData("no-body", true)]
    public async Task Word_XmlErrorsAndWarningsMatchDomBeforeEarlyTextBudget(string mode, bool includeText)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("word-sax-error.docx");
        var xml = mode switch
        {
            "late-close" => Start + Paragraph("early text exceeds three") + "</w:body></w:broken>",
            "skipped-corruption" => Start + Paragraph("early text exceeds three") + "<w:drawing><w:p></w:drawing>" + End,
            "doctype" => "<!DOCTYPE w:document [<!ENTITY x 'entity'>]>" + Start + Paragraph("early text exceeds three") + End,
            "part-limit" => Start + Paragraph("early text exceeds three") + "<!--" + new string('x', (int)ReadOnlyExtraction.MaxPartCharacters) + "-->" + End,
            "wrong-root" => "<unexpected/>",
            "wrong-root-malformed" => "<unexpected><unfinished>",
            "empty-part" => string.Empty,
            "short-part" => "<w",
            "no-body" => $"<w:document xmlns:w=\"{WordNamespace}\"><w:background/></w:document>",
            _ => throw new ArgumentException("Unknown fixture.", nameof(mode))
        };
        CreateWord(path, xml);
        var scope = new DocumentScopeOptions(WordBody: includeText, WordTables: includeText);
        var limits = new SearchResourceLimits { MaxSegmentCharacters = 3 };
        var expected = await Capture(context => Legacy(path, context, scope), limits);
        var actual = await Capture(context => new WordTextExtractor(scope).ExtractAsync(path, context), limits);
        AssertEquivalent(expected, actual);
        Assert.IsType<ExtractionException>(actual.Error);
        Assert.Empty(actual.Segments);
        Assert.Empty(actual.Warnings);
    }

    [Theory]
    [InlineData("    ", 3)]
    [InlineData("🍉x", 2)]
    [InlineData("abcd", 3)]
    public async Task Word_ParagraphBudgetMatchesDomBeforeWhitespaceFilteringAndPublishing(string text, int limit)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("word-sax-text-budget.docx");
        CreateWord(path, Start + Paragraph("ok") + Paragraph(text) + End);
        var limits = new SearchResourceLimits { MaxSegmentCharacters = limit };
        var expected = await Capture(context => Legacy(path, context, new()), limits);
        var actual = await Capture(context => new WordTextExtractor().ExtractAsync(path, context), limits);
        AssertEquivalent(expected, actual);
        Assert.IsType<SearchResourceLimitException>(actual.Error);
        Assert.Empty(actual.Segments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Word_EmptyBodyAndWarningCancellationMatchDom(bool cancelAtWarning)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("word-sax-empty.docx");
        CreateWord(path, Start + End);
        using var expectedCancellation = new CancellationTokenSource();
        using var actualCancellation = new CancellationTokenSource();
        var expected = await Capture(context => Legacy(path, context, new(), expectedCancellation.Token),
            atWarning: () => { if (cancelAtWarning) expectedCancellation.Cancel(); });
        var actual = await Capture(context => new WordTextExtractor().ExtractAsync(path, context, actualCancellation.Token),
            atWarning: () => { if (cancelAtWarning) actualCancellation.Cancel(); });
        AssertEquivalent(expected, actual);
        Assert.Empty(actual.Segments);
        if (cancelAtWarning) Assert.IsAssignableFrom<OperationCanceledException>(actual.Error);
        else Assert.Null(actual.Error);
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";
    private static string Table(string cellContent) => "<w:tbl><w:tr><w:tc>" + cellContent + "</w:tc></w:tr></w:tbl>";

    private static void CreateWord(string path, string xml)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        document.AddMainDocumentPart().FeedData(data);
    }

    private sealed record Outcome(List<TextSegment> Segments, List<string> Warnings, Exception? Error);

    private static async Task<Outcome> Capture(Func<ExtractionContext, IAsyncEnumerable<TextSegment>> read,
        SearchResourceLimits? limits = null, Action? atWarning = null)
    {
        var segments = new List<TextSegment>();
        var warnings = new List<string>();
        var context = new ExtractionContext(message => { warnings.Add(message); atWarning?.Invoke(); }, limits: limits);
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var segment in read(context)) segments.Add(segment);
        });
        return new Outcome(segments, warnings, error);
    }

    private static void AssertEquivalent(Outcome expected, Outcome actual)
    {
        Assert.Equal(expected.Segments, actual.Segments);
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Error?.GetType(), actual.Error?.GetType());
        Assert.Equal(expected.Error?.Message, actual.Error?.Message);
        Assert.Equal(expected.Error?.InnerException?.GetType(), actual.Error?.InnerException?.GetType());
        Assert.Equal(expected.Error?.InnerException?.Message, actual.Error?.InnerException?.Message);
    }

    // Frozen rc11 DOM traversal: do not share the streaming walkers or text fast paths.
    private static IAsyncEnumerable<TextSegment> Legacy(string path, ExtractionContext context, DocumentScopeOptions scope,
        CancellationToken token = default) => ReadOnlyExtraction.Run(path, context, (stream, current, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        OpenXmlPackageBudget.Validate(stream, cancellation);
        using var document = WordprocessingDocument.Open(stream, false, ReadOnlyExtraction.Settings());
        cancellation.ThrowIfCancellationRequested();
        var body = document.MainDocumentPart?.Document?.Body ?? throw new ExtractionException("DOCX 缺少正文。");
        current.Warn(Warning);
        var output = new BoundedSegmentBuffer(maxSegmentCharacters: current.Limits.MaxSegmentCharacters);
        var bodyParagraph = 0;
        var tableNumber = 0;
        Walk(body, null, null, null, ref bodyParagraph);
        cancellation.ThrowIfCancellationRequested();
        return output.Complete();

        void Walk(OpenXmlElement parent, int? table, int? row, int? cell, ref int paragraphNumber)
        {
            foreach (var child in parent.ChildElements)
            {
                cancellation.ThrowIfCancellationRequested();
                if (child is W.Paragraph paragraph)
                {
                    paragraphNumber++;
                    if (table.HasValue ? !scope.WordTables : !scope.WordBody) continue;
                    var text = new BoundedTextBuilder(current.Limits.MaxSegmentCharacters, output.RemainingCharacters);
                    Append(paragraph);
                    var value = text.ToString();
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var source = new SourceLocator(table.HasValue ? SourceKind.WordTableCell : SourceKind.WordParagraph,
                        Paragraph: paragraphNumber, Table: table, Row: row, Cell: cell);
                    output.Add(new TextSegment(table.HasValue ? $"word:t{table}:r{row}:c{cell}:p{paragraphNumber}" : $"word:p{paragraphNumber}", value, source));
                    void Append(OpenXmlElement element)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (Skip(element)) return;
                        if (element is W.Text part) text.Append(part.Text);
                        else if (element is W.TabChar) text.Append('\t');
                        else if (element is W.Break or W.CarriageReturn) text.Append('\n');
                        else foreach (var nested in element.ChildElements) Append(nested);
                    }
                }
                else if (child is W.Table nested)
                {
                    var currentTable = ++tableNumber;
                    var rowNumber = 0;
                    foreach (var tableRow in Blocks<W.TableRow>(nested))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        rowNumber++;
                        var column = (int?)tableRow.TableRowProperties?.GetFirstChild<W.GridBefore>()?.Val?.Value ?? 0;
                        foreach (var tableCell in Blocks<W.TableCell>(tableRow))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var cellParagraph = 0;
                            Walk(tableCell, currentTable, rowNumber, column + 1, ref cellParagraph);
                            column += Math.Max(1, tableCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
                        }
                    }
                }
                else if (!Skip(child)) Walk(child, table, row, cell, ref paragraphNumber);
            }
        }

        IEnumerable<T> Blocks<T>(OpenXmlElement parent) where T : OpenXmlElement
        {
            foreach (var child in parent.ChildElements)
            {
                cancellation.ThrowIfCancellationRequested();
                if (child is T item) yield return item;
                else if (!Skip(child) && child is not (W.Table or W.TableRow or W.TableCell or W.Paragraph))
                    foreach (var nested in Blocks<T>(child)) yield return nested;
            }
        }
    }, token);

    private static bool Skip(OpenXmlElement element) => element.LocalName is
        "drawing" or "pict" or "object" or "txbxContent" or "del" or "moveFrom" or "AlternateContent";
}
