using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public static class SourcePositionFormatter
{
    public static string Format(SourceLocator source) => source.Kind switch
    {
        SourceKind.TextLine => $"行 {source.Line:N0} · 列 {source.Column:N0}",
        SourceKind.CsvField => $"记录 {source.Record:N0} · 字段列 {source.Field:N0} · 字段起始行 {source.Line:N0}",
        SourceKind.WordParagraph => $"正文段落 {source.Paragraph:N0}（结构编号）",
        SourceKind.WordTableCell => $"表格 {source.Table:N0} · 行 {source.Row:N0} · 单元格 {source.Cell:N0} · 段落 {source.Paragraph:N0}",
        SourceKind.ExcelCell => $"工作表 {source.Sheet} · {source.Address}（{source.ValueKind ?? "存储值"}）",
        SourceKind.SlideText => $"幻灯片 {source.Slide:N0} · 对象 {source.ObjectName} · 段落 {source.Paragraph:N0}",
        SourceKind.SlideTableCell => $"幻灯片 {source.Slide:N0} · 表格 {source.ObjectName} · 行 {source.Row:N0} · 单元格 {source.Cell:N0} · 段落 {source.Paragraph:N0}",
        SourceKind.PdfPage => $"PDF 页码 {source.Page:N0}（页内提取文本）",
        _ => "来源位置未提供"
    };
}
