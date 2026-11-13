using LocalDocSearch.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalDocSearch.Extractors;

public sealed class PdfTextExtractor : ITextExtractor
{
    // Scope options currently affect Office formats; PDF extraction remains page text only.
    public PdfTextExtractor(DocumentScopeOptions? options = null) { }

    public IReadOnlyCollection<string> Extensions { get; } = Array.AsReadOnly(new[] { ".pdf" });

    public IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, CancellationToken cancellationToken = default) =>
        ReadOnlyExtraction.Run(path, context, Read, cancellationToken);

    private static List<TextSegment> Read(FileStream stream, ExtractionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(stream);
        token.ThrowIfCancellationRequested();
        context.Warn("PDF 按文件内容顺序提取每页文字；多栏、定位绘制文字的阅读顺序可能与视觉不同，OCR 未启用。");
        var output = new BoundedSegmentBuffer(maxSegmentCharacters: context.Limits.MaxSegmentCharacters);
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            token.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page, false);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
            {
                context.Warn($"PDF 第 {pageNumber} 页没有可提取文字，可能是扫描页；OCR 未启用。");
                continue;
            }
            output.Add(new TextSegment($"pdf:p{pageNumber}", text, new SourceLocator(SourceKind.PdfPage, Page: pageNumber)));
        }
        token.ThrowIfCancellationRequested();
        return output.Complete();
    }
}
