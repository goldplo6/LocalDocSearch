using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace LocalDocSearch.Extractors.Tests;

public sealed class OfficeParagraphFastPathTests
{
    private const string Warning = "PowerPoint 仅搜索幻灯片顶层文本框及表格；备注、组合图形、母版、图表和 SmartArt 不搜索。";

    [Theory]
    [InlineData("single")]
    [InlineData("properties")]
    [InlineData("empty")]
    [InlineData("missing-text")]
    [InlineData("field")]
    [InlineData("rich")]
    [InlineData("duplicate-text")]
    [InlineData("ignored-descendants")]
    [InlineData("two-runs")]
    public void PowerPoint_ParagraphMatchesFrozenDirectChildAlgorithm(string mode)
    {
        var paragraph = Paragraph(mode);
        var expected = FrozenText(paragraph, 100, 100, CancellationToken.None);
        var actual = PowerPointTextExtractor.ParagraphText(paragraph, 100, 100, CancellationToken.None);
        Assert.Equal(expected, actual);
        if (mode == "ignored-descendants") Assert.Equal("kept", actual);
        if (mode == "rich") Assert.Equal("first\t\nfieldlast", actual);
        if (mode == "duplicate-text") Assert.Equal("first", actual);
    }

    [Theory]
    [InlineData("    ", 3, 0L)]
    [InlineData(" \t ", 10, 2L)]
    [InlineData("🍉x", 2, 0L)]
    [InlineData("🍉", 2, 1L)]
    [InlineData("🍉", 2, 2L)]
    [InlineData("", 1, 0L)]
    public void PowerPoint_SingleRunChecksSegmentThenRemainingBeforeWhitespace(string text, int maximum, long remaining)
    {
        var paragraph = new A.Paragraph(new A.ParagraphProperties(), new A.Run(new A.RunProperties(), new A.Text(text)), new A.EndParagraphRunProperties());
        string? expectedValue = null;
        string? actualValue = null;
        var expected = Record.Exception(() => expectedValue = FrozenText(paragraph, maximum, remaining, CancellationToken.None));
        var actual = Record.Exception(() => actualValue = PowerPointTextExtractor.ParagraphText(paragraph, maximum, remaining, CancellationToken.None));
        Assert.Equal(expectedValue, actualValue);
        Assert.Equal(expected?.GetType(), actual?.GetType());
        Assert.Equal(expected?.Message, actual?.Message);
        if (text.Length > maximum) Assert.IsType<SearchResourceLimitException>(actual);
        else if (text.Length > remaining) Assert.IsType<ExtractionException>(actual);
        else Assert.Equal(text, actualValue);
    }

    [Theory]
    [InlineData(0, 10L)]
    [InlineData(-1, 10L)]
    [InlineData(10, -1L)]
    public void PowerPoint_InvalidBudgetsRetainBuilderArgumentFailures(int maximum, long remaining)
    {
        var paragraph = Paragraph("empty");
        var expected = Record.Exception(() => FrozenText(paragraph, maximum, remaining, CancellationToken.None));
        var actual = Record.Exception(() => PowerPointTextExtractor.ParagraphText(paragraph, maximum, remaining, CancellationToken.None));
        Assert.Equal(expected?.GetType(), actual?.GetType());
        Assert.Equal(expected?.Message, actual?.Message);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task PowerPoint_FastPathMatchesDomScopesRelationshipsAndSixObjectPositions(bool text, bool tables)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("paragraph-fast-path.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            var storedFirst = presentation.AddNewPart<SlidePart>();
            storedFirst.Slide = Slide(Shape("存储首张", [Paragraph("single")]));
            var storedSecond = presentation.AddNewPart<SlidePart>();
            var cell = new A.TableCell(new A.TextBody(new OpenXmlElement[] { new A.BodyProperties(), new A.ListStyle(), Paragraph("properties"), Paragraph("rich"), Paragraph("empty") }), new A.TableCellProperties());
            var table = new A.Table(new A.TableRow(cell) { Height = 1L });
            var frame = new P.GraphicFrame(new P.NonVisualGraphicFrameProperties(new P.NonVisualDrawingProperties { Id = 9U, Name = "表格名称" }),
                new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
            storedSecond.Slide = Slide(
                new P.NonVisualGroupShapeProperties(), new P.GroupShapeProperties(),
                Shape("首对象", [Paragraph("single"), Paragraph("empty"), Paragraph("properties"), Paragraph("field"), Paragraph("rich"), Paragraph("ignored-descendants")]),
                new P.GraphicFrame(), new P.GroupShape(Shape("组合禁搜", [TextParagraph("组合禁搜")])), new P.Picture(),
                new P.ConnectionShape(), new P.ContentPart(), Shape("第七对象", [TextParagraph("tail")]), frame,
                new P.ExtensionListWithModification());
            storedSecond.AddNewPart<NotesSlidePart>().NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(Shape("备注禁搜", [TextParagraph("备注禁搜")]))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(storedSecond) },
                new P.SlideId { Id = 257U, RelationshipId = presentation.GetIdOfPart(storedFirst) });
        }
        var original = await File.ReadAllBytesAsync(path);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var scope = new DocumentScopeOptions(PresentationText: text, PresentationTables: tables);
        var expected = await Capture(context => Legacy(path, context, scope));
        var actual = await Capture(context => new PowerPointTextExtractor(scope).ExtractAsync(path, context));
        AssertEquivalent(expected, actual);
        Assert.Null(actual.Error);
        Assert.Equal(new[] { Warning }, actual.Warnings);
        Assert.DoesNotContain(actual.Segments, segment => segment.Text.Contains("禁搜", StringComparison.Ordinal));
        if (text)
        {
            Assert.Equal("ppt:s1:o7:r:c:p1", Assert.Single(actual.Segments, item => item.Text == "tail").Id);
            Assert.Equal("首对象", Assert.Single(actual.Segments, item => item.Id == "ppt:s1:o1:r:c:p3").Source.ObjectName);
            Assert.Equal("ppt:s2:o1:r:c:p1", Assert.Single(actual.Segments, item => item.Source.Slide == 2).Id);
        }
        if (tables)
        {
            var item = Assert.Single(actual.Segments, item => item.Id == "ppt:s1:o8:r1:c1:p2");
            Assert.Equal("表格名称", item.Source.ObjectName);
            Assert.Equal("first\t\nfieldlast", item.Text);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        using var released = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [Theory]
    [InlineData("    ", false)]
    [InlineData("🍉x", false)]
    [InlineData("    ", true)]
    [InlineData("ok", true)]
    public async Task PowerPoint_LimitsAndLateMalformedSlidesRetainErrorsWarningsAndZeroPublication(string text, bool malformedLate)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("paragraph-fast-failure.pptx");
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation(new P.SlideIdList());
            var first = presentation.AddNewPart<SlidePart>();
            first.Slide = Slide(Shape("原始对象", [TextParagraph("ok"), TextParagraph(text)]));
            presentation.Presentation.SlideIdList!.Append(new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(first) });
            if (malformedLate)
            {
                var late = presentation.AddNewPart<SlidePart>();
                using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld>"));
                late.FeedData(source);
                presentation.Presentation.SlideIdList.Append(new P.SlideId { Id = 257U, RelationshipId = presentation.GetIdOfPart(late) });
            }
        }
        var limit = text == "🍉x" ? 2 : 3;
        var limits = new SearchResourceLimits { MaxSegmentCharacters = limit };
        var expected = await Capture(context => Legacy(path, context, new()), limits);
        var actual = await Capture(context => new PowerPointTextExtractor().ExtractAsync(path, context), limits);
        AssertEquivalent(expected, actual);
        Assert.NotNull(actual.Error);
        Assert.Empty(actual.Segments);
        if (text.Length > limit) Assert.IsType<SearchResourceLimitException>(actual.Error);
        else Assert.IsType<ExtractionException>(actual.Error);
    }

    [Fact]
    public void PowerPoint_FastAndRichParagraphsObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var mode in new[] { "single", "empty", "rich" })
            Assert.ThrowsAny<OperationCanceledException>(() => PowerPointTextExtractor.ParagraphText(Paragraph(mode), 100, 100, cancellation.Token));
    }

    private static P.Slide Slide(params OpenXmlElement[] objects) => new(new P.CommonSlideData(new P.ShapeTree(objects)));
    private static P.Shape Shape(string name, A.Paragraph[] paragraphs) => new(
        new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = name }), new P.ShapeProperties(),
        new P.TextBody(new OpenXmlElement[] { new A.BodyProperties(), new A.ListStyle() }.Concat(paragraphs)));
    private static A.Paragraph TextParagraph(string text) => new(new A.Run(new A.Text(text)));

    private static A.Paragraph Paragraph(string mode) => mode switch
    {
        "single" => TextParagraph("single 🍉"),
        "properties" => new A.Paragraph(new A.ParagraphProperties(), new A.Run(new A.RunProperties { Bold = true }, new A.Text("styled")), new A.EndParagraphRunProperties()),
        "empty" => new A.Paragraph(new A.ParagraphProperties(), new A.EndParagraphRunProperties()),
        "missing-text" => new A.Paragraph(new A.Run(new A.RunProperties())),
        "field" => new A.Paragraph(new A.Field(new A.Text("field")) { Id = "{00000000-0000-0000-0000-000000000001}" }),
        "rich" => new A.Paragraph(new A.ParagraphProperties(), new A.Run(new A.Text("first\t")), new A.Break(),
            new A.Field(new A.Text("field")) { Id = "{00000000-0000-0000-0000-000000000002}" }, new A.Run(new A.Text("last")), new A.EndParagraphRunProperties()),
        "duplicate-text" => new A.Paragraph(new A.Run(new A.Text("first"), new A.Text("ignored duplicate"))),
        "ignored-descendants" => new A.Paragraph(new A.ParagraphProperties(new A.Text("descendant禁搜")), new A.Text("direct禁搜"), new A.Run(new A.Text("kept"))),
        "two-runs" => new A.Paragraph(new A.Run(), new A.Run(new A.Text("second"))),
        _ => throw new ArgumentException("Unknown paragraph fixture.", nameof(mode))
    };

    private sealed record Outcome(List<TextSegment> Segments, List<string> Warnings, Exception? Error);

    private static async Task<Outcome> Capture(Func<ExtractionContext, IAsyncEnumerable<TextSegment>> read, SearchResourceLimits? limits = null)
    {
        var segments = new List<TextSegment>();
        var warnings = new List<string>();
        var context = new ExtractionContext(warnings.Add, limits: limits);
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

    private static string FrozenText(A.Paragraph paragraph, int maximum, long remaining, CancellationToken token)
    {
        var text = new BoundedTextBuilder(maximum, remaining);
        foreach (var child in paragraph.ChildElements)
        {
            token.ThrowIfCancellationRequested();
            if (child is A.Run run) text.Append(run.Text?.Text);
            else if (child is A.Field field) text.Append(field.Text?.Text);
            else if (child is A.Break) text.Append('\n');
        }
        return text.ToString();
    }

    // Frozen rc11 traversal intentionally retains its own direct-child text builder.
    private static IAsyncEnumerable<TextSegment> Legacy(string path, ExtractionContext context, DocumentScopeOptions scope) =>
        ReadOnlyExtraction.Run(path, context, (stream, current, token) =>
        {
            token.ThrowIfCancellationRequested();
            OpenXmlPackageBudget.Validate(stream, token);
            using var document = PresentationDocument.Open(stream, false, ReadOnlyExtraction.Settings());
            token.ThrowIfCancellationRequested();
            var presentation = document.PresentationPart ?? throw new ExtractionException("PPTX 缺少演示文稿。");
            var slides = presentation.Presentation?.SlideIdList;
            var output = new BoundedSegmentBuffer(maxSegmentCharacters: current.Limits.MaxSegmentCharacters);
            current.Warn(Warning);
            if (slides is null) return output.Complete();
            var slideNumber = 0;
            foreach (var slideId in slides.Elements<P.SlideId>())
            {
                token.ThrowIfCancellationRequested();
                slideNumber++;
                var relation = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relation) || presentation.GetPartById(relation) is not SlidePart slide)
                    throw new ExtractionException($"第 {slideNumber} 张幻灯片的关系无效。");
                var tree = slide.Slide?.CommonSlideData?.ShapeTree;
                token.ThrowIfCancellationRequested();
                if (tree is null) continue;
                var objectNumber = 0;
                foreach (var item in tree.ChildElements)
                {
                    token.ThrowIfCancellationRequested();
                    if (item is not (P.Shape or P.GraphicFrame or P.GroupShape or P.Picture or P.ConnectionShape or P.ContentPart)) continue;
                    objectNumber++;
                    if (scope.PresentationText && item is P.Shape shape && shape.TextBody is { } textBody)
                    {
                        var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
                        var paragraph = 0;
                        foreach (var block in textBody.Elements<A.Paragraph>()) Add(block, ++paragraph, null, null, name, SourceKind.SlideText);
                    }
                    else if (scope.PresentationTables && item is P.GraphicFrame frame)
                    {
                        var name = frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value;
                        var table = frame.Graphic?.GraphicData?.GetFirstChild<A.Table>();
                        if (table is null) continue;
                        var row = 0;
                        foreach (var tableRow in table.Elements<A.TableRow>())
                        {
                            token.ThrowIfCancellationRequested();
                            row++;
                            var column = 0;
                            foreach (var cell in tableRow.Elements<A.TableCell>())
                            {
                                token.ThrowIfCancellationRequested();
                                column++;
                                if (cell.TextBody is null) continue;
                                var paragraph = 0;
                                foreach (var block in cell.TextBody.Elements<A.Paragraph>()) Add(block, ++paragraph, row, column, name, SourceKind.SlideTableCell);
                            }
                        }
                    }

                    void Add(A.Paragraph paragraph, int paragraphNumber, int? row, int? column, string? name, SourceKind kind)
                    {
                        token.ThrowIfCancellationRequested();
                        var value = FrozenText(paragraph, current.Limits.MaxSegmentCharacters, output.RemainingCharacters, token);
                        if (string.IsNullOrWhiteSpace(value)) return;
                        output.Add(new TextSegment($"ppt:s{slideNumber}:o{objectNumber}:r{row}:c{column}:p{paragraphNumber}", value,
                            new SourceLocator(kind, Paragraph: paragraphNumber, Slide: slideNumber, ObjectName: name, Row: row, Cell: column)));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return output.Complete();
        }, CancellationToken.None);
}
