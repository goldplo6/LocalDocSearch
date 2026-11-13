using LocalDocSearch.Core;
using LocalDocSearch.Fixtures;

namespace LocalDocSearch.Extractors.Tests;

public sealed class SampleCorpus : IDisposable
{
    private readonly TestDirectory _directory = new();
    public string Root => _directory.Root;
    public SampleCorpus() => SampleDocuments.CreateAll(Root);
    public string FileFor(string extension) => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
        .Single(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase));
    public void Dispose() => _directory.Dispose();
}

internal sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Extractors.Tests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Root);
    public string FileFor(string name) => Path.Combine(Root, name);
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

internal static class ExtractionTestSupport
{
    public static ITextExtractor For(string extension) => DocumentExtractorCatalog.CreateDefault()
        .Single(extractor => extractor.Extensions.Contains(extension));

    public static async Task<List<TextSegment>> ReadAsync(string path, List<string>? warnings = null, CancellationToken token = default)
    {
        var output = new List<TextSegment>();
        await foreach (var item in For(Path.GetExtension(path)).ExtractAsync(path, new ExtractionContext(message => warnings?.Add(message)), token))
            output.Add(item);
        return output;
    }

    public static async Task<(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues)> SearchAsync(
        string directory, IReadOnlyList<string> terms, IReadOnlyList<string>? extensions = null,
        IEnumerable<ITextExtractor>? extractors = null, CancellationToken token = default)
    {
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var engine = new TextSearchEngine(extractors ?? DocumentExtractorCatalog.CreateDefault());
        var summary = await engine.SearchAsync(new SearchRequest(directory, terms, Extensions: extensions),
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add), cancellationToken: token);
        return (summary, results, issues);
    }
}
