using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LocalDocSearch.Extractors;

public sealed class ExcelTextExtractor(DocumentScopeOptions? options = null) : ITextExtractor
{
    private readonly DocumentScopeOptions _options = options ?? new();
    public IReadOnlyCollection<string> Extensions { get; } = Array.AsReadOnly(new[] { ".xlsx" });

    public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default) =>
        ReadOnlyExtraction.Run(path, context, Read, cancellationToken);

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
        if (workbook.SharedStringTablePart is { } shared)
            foreach (var item in ReadSharedStrings(shared, token))
            {
                token.ThrowIfCancellationRequested();
                if (sharedStrings.Count >= BoundedSegmentBuffer.DefaultMaxSegments)
                    throw new ExtractionException($"Excel 共享字符串数量超过上限（{BoundedSegmentBuffer.DefaultMaxSegments:N0}）；未发布部分文档。");
                // Lookup storage has its own cumulative budget. The per-result segment limit
                // still applies only when a cell actually references the shared string.
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ReadCells(worksheet, token))
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
        token.ThrowIfCancellationRequested();
        return output.Complete();
    }

    private static OpenXmlPartReader PartReader(OpenXmlPart part) => new(part, new OpenXmlPartReaderOptions
    {
        // Set this explicitly: a SAX reader must enforce the same XML character
        // budget as the package's DOM loader, including skipped/non-result XML.
        MaxCharactersInPart = ReadOnlyExtraction.Settings().MaxCharactersInPart
    });

    private static bool ValidatePart(OpenXmlPart part, Type rootType, CancellationToken token)
    {
        // Match the SDK DOM loader: parts shorter than the shortest XML root
        // have no root element (even when those bytes are not valid XML).
        using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
            if (stream.Length < 4) return false;

        using var reader = PartReader(part);
        bool hasRoot;
        try { hasRoot = reader.Read(); }
        catch (InvalidDataException)
        {
            // A comment/declaration-only part has no DOM root, whereas the SAX
            // reader reports PartIsEmpty. Ask the DOM loader for its exact result.
            _ = part.RootElement;
            return false;
        }
        if (!hasRoot || reader.ElementType != rootType)
        {
            // Preserve the SDK's root-name validation and exception wrapping.
            // This fallback is only used for a part with an unexpected root.
            _ = part.RootElement;
            return false;
        }
        // The old DOM parsed the entire part before any cell/item processing.
        // A separate streaming pass preserves malformed XML and part-limit
        // precedence over warnings, duplicate coordinates and lookup errors.
        while (reader.Read()) token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        return true;
    }

    private static IEnumerable<S.SharedStringItem> ReadSharedStrings(SharedStringTablePart part, CancellationToken token)
    {
        if (!ValidatePart(part, typeof(S.SharedStringTable), token)) yield break;
        using var reader = PartReader(part);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsStartElement && reader.Depth == 1 && reader.ElementType == typeof(S.SharedStringItem))
                // LoadCurrentElement leaves the cursor at this item's end; the
                // next Read advances once, so adjacent and empty items survive.
                yield return (S.SharedStringItem)reader.LoadCurrentElement()!;
        }
    }

    private static IEnumerable<S.Cell> ReadCells(WorksheetPart part, CancellationToken token)
    {
        if (!ValidatePart(part, typeof(S.Worksheet), token)) yield break;
        using var reader = PartReader(part);
        var foundData = false;
        var inData = false;
        var inRow = false;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth == 1)
            {
                // Recompute on every direct sibling, including empty elements;
                // neither row nor data state may leak into an unknown wrapper.
                inRow = false;
                inData = reader.IsStartElement && !foundData && reader.ElementType == typeof(S.SheetData);
                if (inData) foundData = true;
            }
            else if (inData && reader.Depth == 2)
                inRow = reader.IsStartElement && reader.ElementType == typeof(S.Row);
            else if (inData && inRow && reader.IsStartElement && reader.Depth == 3 && reader.ElementType == typeof(S.Cell))
                yield return (S.Cell)reader.LoadCurrentElement()!;
        }
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

    internal static bool TryCoordinates(string? address, out int row, out int column)
    {
        row = column = 0;
        if (address is null) return false;
        ReadOnlySpan<char> reference = address.AsSpan();
        // The original regex's $ also matched immediately before one final LF.
        if (!reference.IsEmpty && reference[^1] == '\n') reference = reference[..^1];
        if (reference.Length is < 2 or > 10) return false;

        var letters = 0;
        while (letters < reference.Length &&
            (reference[letters] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) letters++;
        var digits = reference.Length - letters;
        if (letters is < 1 or > 3 || digits is < 1 or > 7 || reference[letters] is < '1' or > '9') return false;

        foreach (var digit in reference[letters..])
            if (digit is < '0' or > '9') return false;
        // Keep the original current-culture integer conversion and failure outputs,
        // including custom number-format signs, without allocating a substring.
        if (!int.TryParse(reference[letters..], out row) || row > 1048576) return false;
        foreach (var letter in reference[..letters])
            column = column * 26 + (letter is >= 'a' and <= 'z' ? letter - 'a' : letter - 'A') + 1;
        return column <= 16384;
    }
}
