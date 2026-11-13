using LocalDocSearch.Core;

namespace LocalDocSearch.Extractors;

public static class DocumentExtractorCatalog
{
    public static IReadOnlyList<ITextExtractor> CreateDefault(DocumentScopeOptions? options = null) => Array.AsReadOnly<ITextExtractor>(
    [
        new PlainTextExtractor(),
        new CsvTextExtractor(),
        new WordTextExtractor(options),
        new ExcelTextExtractor(options),
        new PowerPointTextExtractor(options),
        new PdfTextExtractor(options)
    ]);
}
