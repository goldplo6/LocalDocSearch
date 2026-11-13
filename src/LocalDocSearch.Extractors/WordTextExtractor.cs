using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LocalDocSearch.Extractors;

/// <summary>Body structure only. Paragraph numbers are structural hints, never rendered pages.</summary>
public sealed class WordTextExtractor(DocumentScopeOptions? options = null) : ITextExtractor
{
    private readonly DocumentScopeOptions _options = options ?? new();
    public IReadOnlyCollection<string> Extensions { get; } = Array.AsReadOnly(new[] { ".docx" });

    public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default) =>
        ReadOnlyExtraction.Run(path, context, Read, cancellationToken);

    private List<TextSegment> Read(FileStream stream, ExtractionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OpenXmlPackageBudget.Validate(stream, token);
        using var document = WordprocessingDocument.Open(stream, false, ReadOnlyExtraction.Settings());
        token.ThrowIfCancellationRequested();
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new ExtractionException("DOCX 缺少正文。");
        context.Warn("Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。");
        var output = new BoundedSegmentBuffer(maxSegmentCharacters: context.Limits.MaxSegmentCharacters);
        var bodyParagraph = 0;
        var tableNumber = 0;
        WalkBlocks(body, null, null, null, ref bodyParagraph);
        token.ThrowIfCancellationRequested();
        return output.Complete();

        void WalkBlocks(OpenXmlElement parent, int? table, int? row, int? cell, ref int paragraphNumber)
        {
            foreach (var child in parent.ChildElements)
            {
                token.ThrowIfCancellationRequested();
                if (child is W.Paragraph paragraph)
                {
                    paragraphNumber++;
                    // Count excluded paragraphs as well, so toggling scope never renumbers retained segments.
                    if (table.HasValue ? !_options.WordTables : !_options.WordBody) continue;
                    var text = ParagraphText(paragraph, context.Limits.MaxSegmentCharacters, output.RemainingCharacters, token);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var source = new SourceLocator(table.HasValue ? SourceKind.WordTableCell : SourceKind.WordParagraph,
                        Paragraph: paragraphNumber, Table: table, Row: row, Cell: cell);
                    var id = table.HasValue
                        ? $"word:t{table}:r{row}:c{cell}:p{paragraphNumber}" : $"word:p{paragraphNumber}";
                    output.Add(new TextSegment(id, text, source));
                }
                else if (child is W.Table nested)
                {
                    var currentTable = ++tableNumber;
                    var rowNumber = 0;
                    foreach (var tableRow in Blocks<W.TableRow>(nested, token))
                    {
                        token.ThrowIfCancellationRequested();
                        rowNumber++;
                        var column = (int?)tableRow.TableRowProperties?.GetFirstChild<W.GridBefore>()?.Val?.Value ?? 0;
                        foreach (var tableCell in Blocks<W.TableCell>(tableRow, token))
                        {
                            token.ThrowIfCancellationRequested();
                            var cellParagraph = 0;
                            var columnNumber = column + 1;
                            WalkBlocks(tableCell, currentTable, rowNumber, columnNumber, ref cellParagraph);
                            var span = tableCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                            column += Math.Max(1, span);
                        }
                    }
                }
                else if (!Skip(child)) WalkBlocks(child, table, row, cell, ref paragraphNumber);
            }
        }
    }

    private static IEnumerable<T> Blocks<T>(OpenXmlElement parent, CancellationToken token) where T : OpenXmlElement
    {
        foreach (var child in parent.ChildElements)
        {
            token.ThrowIfCancellationRequested();
            if (child is T item) yield return item;
            else if (!Skip(child) && child is not (W.Table or W.TableRow or W.TableCell or W.Paragraph))
                foreach (var itemInWrapper in Blocks<T>(child, token)) yield return itemInWrapper;
        }
    }

    private static bool Skip(OpenXmlElement element) => element.LocalName is
        "drawing" or "pict" or "object" or "txbxContent" or "del" or "moveFrom" or "AlternateContent";

    private static string ParagraphText(W.Paragraph paragraph, int maxSegmentCharacters, long remainingCharacters, CancellationToken token)
    {
        var text = new BoundedTextBuilder(maxSegmentCharacters, remainingCharacters);
        Append(paragraph);
        return text.ToString();
        void Append(OpenXmlElement element)
        {
            token.ThrowIfCancellationRequested();
            if (Skip(element)) return;
            if (element is W.Text value) text.Append(value.Text);
            else if (element is W.TabChar) text.Append('\t');
            else if (element is W.Break or W.CarriageReturn) text.Append('\n');
            else foreach (var child in element.ChildElements) Append(child);
        }
    }
}
