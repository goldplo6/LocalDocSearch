using System.Text;
using LocalDocSearch.Core;

namespace LocalDocSearch.Core.Tests;

/// <summary>Every test owns a unique, synthetic temporary tree and deletes only that tree.</summary>
internal sealed class SearchTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Core.Tests", Guid.NewGuid().ToString("N"));

    public SearchTestDirectory() => Directory.CreateDirectory(Root);

    public async Task<string> WriteAsync(string relativePath, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, encoding ?? new UTF8Encoding(false, true));
        return path;
    }

    public async Task<string> WriteBytesAsync(string relativePath, byte[] content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

internal sealed class SearchCapture
{
    public List<FileResult> Results { get; } = [];
    public List<SearchIssue> Issues { get; } = [];
    public List<SearchProgress> Progress { get; } = [];

    public Task<SearchSummary> RunAsync(SearchRequest request, CancellationToken token = default) =>
        new TextSearchEngine().SearchAsync(request,
            new InlineProgress<SearchProgress>(Progress.Add),
            new InlineProgress<FileResult>(Results.Add),
            new InlineProgress<SearchIssue>(Issues.Add), token);
}
