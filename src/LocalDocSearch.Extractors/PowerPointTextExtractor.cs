using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace LocalDocSearch.Extractors;

public sealed class PowerPointTextExtractor(DocumentScopeOptions? options = null) : ITextExtractor
{
    private readonly DocumentScopeOptions _options = options ?? new();
    public IReadOnlyCollection<string> Extensions { get; } = Array.AsReadOnly(new[] { ".pptx" });

    public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default) =>
        ReadOnlyExtraction.Run(path, context, Read, cancellationToken);

    private List<TextSegment> Read(FileStream stream, ExtractionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OpenXmlPackageBudget.Validate(stream, token);
        using var document = PresentationDocument.Open(stream, false, ReadOnlyExtraction.Settings());
        token.ThrowIfCancellationRequested();
        var presentation = document.PresentationPart ?? throw new ExtractionException("PPTX 缺少演示文稿。");
        var slides = presentation.Presentation?.SlideIdList;
        var output = new BoundedSegmentBuffer(maxSegmentCharacters: context.Limits.MaxSegmentCharacters);
        context.Warn("PowerPoint 仅搜索幻灯片顶层文本框及表格；备注、组合图形、母版、图表和 SmartArt 不搜索。");
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
                // Shape-tree properties and extension lists are metadata, not top-level objects.
                if (item is not (P.Shape or P.GraphicFrame or P.GroupShape or P.Picture or P.ConnectionShape or P.ContentPart)) continue;
                objectNumber++;
                if (_options.PresentationText && item is P.Shape shape && shape.TextBody is { } textBody)
                {
                    var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
                    var paragraph = 0;
                    foreach (var block in textBody.Elements<A.Paragraph>())
                        Add(block, ++paragraph, null, null, name, SourceKind.SlideText);
                }
                else if (_options.PresentationTables && item is P.GraphicFrame frame)
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
                            foreach (var block in cell.TextBody.Elements<A.Paragraph>())
                                Add(block, ++paragraph, row, column, name, SourceKind.SlideTableCell);
                        }
                    }
                }

                void Add(A.Paragraph paragraph, int paragraphNumber, int? row, int? column, string? name, SourceKind kind)
                {
                    token.ThrowIfCancellationRequested();
                    var text = new BoundedTextBuilder(context.Limits.MaxSegmentCharacters, output.RemainingCharacters);
                    foreach (var child in paragraph.ChildElements)
                    {
                        token.ThrowIfCancellationRequested();
                        if (child is A.Run run) text.Append(run.Text?.Text);
                        else if (child is A.Field field) text.Append(field.Text?.Text);
                        else if (child is A.Break) text.Append('\n');
                    }
                    var value = text.ToString();
                    if (string.IsNullOrWhiteSpace(value)) return;
                    var id = $"ppt:s{slideNumber}:o{objectNumber}:r{row}:c{column}:p{paragraphNumber}";
                    output.Add(new TextSegment(id, value, new SourceLocator(kind, Paragraph: paragraphNumber,
                        Slide: slideNumber, ObjectName: name, Row: row, Cell: column)));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return output.Complete();
    }
}
