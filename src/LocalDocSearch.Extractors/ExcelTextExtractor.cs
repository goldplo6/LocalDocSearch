using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LocalDocSearch.Extractors;

public sealed class ExcelTextExtractor(DocumentScopeOptions? options = null) : ITextExtractor
{
    private readonly DocumentScopeOptions _options = options ?? new();
    private static readonly Regex AddressPattern = new("^([A-Za-z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
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
        if (workbook.SharedStringTablePart?.SharedStringTable is { } shared)
            foreach (var item in shared.Elements<S.SharedStringItem>())
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
