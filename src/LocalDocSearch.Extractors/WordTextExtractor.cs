using System.Xml;
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
        var main = document.MainDocumentPart ?? throw new ExtractionException("DOCX 缺少正文。");
        // LoadFromPart treats XML parts shorter than four bytes as missing roots.
        using (var partStream = main.GetStream(FileMode.Open, FileAccess.Read))
            if (partStream.Length < 4) throw new ExtractionException("DOCX 缺少正文。");
        using var reader = new OpenXmlPartReader(main, new OpenXmlPartReaderOptions
        {
            MaxCharactersInPart = ReadOnlyExtraction.MaxPartCharacters
        });
        if (!reader.Read() || reader.ElementType != typeof(W.Document))
        {
            // Retain the SDK's root-type failure, including its diagnostic, on invalid parts.
            _ = main.Document;
            throw new ExtractionException("DOCX 缺少正文。");
        }
        // The DOM used to validate the entire part before warnings and text budgets. An
        // XML-only pass retains that error priority without retaining the whole document DOM.
        ValidateXml(main, token);
        var output = new BoundedSegmentBuffer(maxSegmentCharacters: context.Limits.MaxSegmentCharacters);
        var bodyParagraph = 0;
        var tableNumber = 0;
        var foundBody = false;
        reader.Read();
        while (!reader.EOF && reader.Depth > 0)
        {
            token.ThrowIfCancellationRequested();
            if (!foundBody && reader.IsStartElement && reader.Depth == 1 && reader.ElementType == typeof(W.Body))
            {
                foundBody = true;
                context.Warn("Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。");
                WalkReaderBlocks(ref bodyParagraph);
            }
            else reader.Skip();
        }
        if (!foundBody) throw new ExtractionException("DOCX 缺少正文。");
        token.ThrowIfCancellationRequested();
        return output.Complete();

        // Each reader walker consumes its complete parent and leaves the next sibling (or
        // the parent's end tag) current. Wrappers stay streaming, even around a whole body.
        void WalkReaderBlocks(ref int paragraphNumber)
        {
            var depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && reader.Depth > depth)
            {
                token.ThrowIfCancellationRequested();
                if (!reader.IsStartElement) { reader.Read(); continue; }
                if (reader.ElementType == typeof(W.Paragraph))
                {
                    paragraphNumber++;
                    if (!_options.WordBody) { reader.Skip(); continue; }
                    var paragraph = (W.Paragraph)reader.LoadCurrentElement()!;
                    Add(paragraph, null, null, null, paragraphNumber);
                    reader.Read();
                }
                else if (reader.ElementType == typeof(W.Table))
                {
                    var currentTable = ++tableNumber;
                    var rowNumber = 0;
                    WalkReaderRows(currentTable, ref rowNumber);
                }
                else if (Skip(reader.LocalName)) reader.Skip();
                else WalkReaderBlocks(ref paragraphNumber);
            }
            if (!reader.EOF) reader.Read();
        }

        void WalkReaderRows(int table, ref int rowNumber)
        {
            var depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && reader.Depth > depth)
            {
                token.ThrowIfCancellationRequested();
                if (!reader.IsStartElement) { reader.Read(); continue; }
                if (reader.ElementType == typeof(W.TableRow))
                {
                    // A row-sized DOM preserves properties occurring after wrapped cells,
                    // while avoiding a DOM for an entire multi-row table.
                    var row = (W.TableRow)reader.LoadCurrentElement()!;
                    WalkRow(row, table, ++rowNumber);
                    reader.Read();
                }
                else if (Skip(reader.LocalName) || reader.ElementType == typeof(W.Table) ||
                    reader.ElementType == typeof(W.TableCell) || reader.ElementType == typeof(W.Paragraph)) reader.Skip();
                else WalkReaderRows(table, ref rowNumber);
            }
            if (!reader.EOF) reader.Read();
        }

        void WalkRow(W.TableRow tableRow, int table, int row)
        {
            token.ThrowIfCancellationRequested();
            var column = (int?)tableRow.TableRowProperties?.GetFirstChild<W.GridBefore>()?.Val?.Value ?? 0;
            foreach (var tableCell in Blocks<W.TableCell>(tableRow, token))
            {
                token.ThrowIfCancellationRequested();
                var cellParagraph = 0;
                WalkBlocks(tableCell, table, row, column + 1, ref cellParagraph);
                var span = tableCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                column += Math.Max(1, span);
            }
        }

        void WalkBlocks(OpenXmlElement parent, int? table, int? row, int? cell, ref int paragraphNumber)
        {
            foreach (var child in parent.ChildElements)
            {
                token.ThrowIfCancellationRequested();
                if (child is W.Paragraph paragraph)
                {
                    paragraphNumber++;
                    Add(paragraph, table, row, cell, paragraphNumber);
                }
                else if (child is W.Table nested)
                {
                    var currentTable = ++tableNumber;
                    var rowNumber = 0;
                    foreach (var tableRow in Blocks<W.TableRow>(nested, token))
                        WalkRow(tableRow, currentTable, ++rowNumber);
                }
                else if (!Skip(child.LocalName)) WalkBlocks(child, table, row, cell, ref paragraphNumber);
            }
        }

        void Add(W.Paragraph paragraph, int? table, int? row, int? cell, int paragraphNumber)
        {
            // Count excluded paragraphs as well, so toggling scope never renumbers retained segments.
            if (table.HasValue ? !_options.WordTables : !_options.WordBody) return;
            var text = ParagraphText(paragraph, context.Limits.MaxSegmentCharacters, output.RemainingCharacters, token);
            if (string.IsNullOrWhiteSpace(text)) return;
            var source = new SourceLocator(table.HasValue ? SourceKind.WordTableCell : SourceKind.WordParagraph,
                Paragraph: paragraphNumber, Table: table, Row: row, Cell: cell);
            var id = table.HasValue
                ? $"word:t{table}:r{row}:c{cell}:p{paragraphNumber}" : $"word:p{paragraphNumber}";
            output.Add(new TextSegment(id, text, source));
        }
    }

    private static void ValidateXml(MainDocumentPart part, CancellationToken token)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = ReadOnlyExtraction.MaxPartCharacters
        });
        while (reader.Read()) token.ThrowIfCancellationRequested();
    }

    private static IEnumerable<T> Blocks<T>(OpenXmlElement parent, CancellationToken token) where T : OpenXmlElement
    {
        foreach (var child in parent.ChildElements)
        {
            token.ThrowIfCancellationRequested();
            if (child is T item) yield return item;
            else if (!Skip(child.LocalName) && child is not (W.Table or W.TableRow or W.TableCell or W.Paragraph))
                foreach (var itemInWrapper in Blocks<T>(child, token)) yield return itemInWrapper;
        }
    }

    private static bool Skip(string localName) => localName is
        "drawing" or "pict" or "object" or "txbxContent" or "del" or "moveFrom" or "AlternateContent";

    private static string ParagraphText(W.Paragraph paragraph, int maxSegmentCharacters, long remainingCharacters, CancellationToken token)
    {
        var text = new BoundedTextBuilder(maxSegmentCharacters, remainingCharacters);
        Append(paragraph);
        return text.ToString();
        void Append(OpenXmlElement element)
        {
            token.ThrowIfCancellationRequested();
            if (Skip(element.LocalName)) return;
            if (element is W.Text value) text.Append(value.Text);
            else if (element is W.TabChar) text.Append('\t');
            else if (element is W.Break or W.CarriageReturn) text.Append('\n');
            else foreach (var child in element.ChildElements) Append(child);
        }
    }
}
