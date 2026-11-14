using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class FixedOverheadCoreTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 255)]
    [InlineData(1, 256)]
    [InlineData(2, 0)]
    [InlineData(2, 255)]
    [InlineData(2, 256)]
    [InlineData(4, 0)]
    [InlineData(4, 255)]
    [InlineData(4, 256)]
    public async Task CandidateSetIsCreatedOnlyWhenASegmentReachesThreshold(int width, int length)
    {
        using var directory = new SearchTestDirectory();
        for (int index = 0; index < 8; index++)
            await directory.WriteAsync($"{index}.txt", new string('x', length));
        var engine = new TextSearchEngine([new PlainTextExtractor()]);
        var request = new SearchRequest(directory.Root,
            Enumerable.Range(0, 64).Select(index => $"missing-{index}").ToArray(), SearchOperator.Or);
        // Observe the actual query instance that is executed, including its shared worker state.
        var query = typeof(TextSearchEngine).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, [request])!;
        var lazy = Assert.IsType<Lazy<SearchValues<string>>>(query.GetType().GetProperty("LiteralCandidates")!.GetValue(query));
        Assert.False(lazy.IsValueCreated);
        var name = width == 1 ? "SearchCoreAsync" : "SearchConcurrentAsync";
        object?[] arguments = width == 1
            ? [query, null, null, null, CancellationToken.None, null]
            : [query, null, null, null, CancellationToken.None, null, width];
        var task = Assert.IsAssignableFrom<Task<SearchSummary>>(typeof(TextSearchEngine)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, arguments));
        var summary = await task;
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal(8, summary.Progress.ScannedFiles);
        Assert.Equal(0, summary.Progress.Occurrences);
        Assert.Equal(length >= 256, lazy.IsValueCreated);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 42)]
    [InlineData(true, 1)]
    [InlineData(true, 42)]
    public async Task TextLocationsKeepExactColumnsAndPreviewIdentity(bool regex, int originalColumn)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("source.probe", "unchanged source");
        var source = new SourceLocation(7, originalColumn);
        var extractor = new Segments([new("original-id", "hit hit", source)]);
        var results = new List<FileResult>();
        var request = new SearchRequest(directory.Root, regex ? [] : ["hit"], SearchOperator.Or,
            ContextCharacters: 3)
        { MatchMode = regex ? SearchMatchMode.RegularExpression : SearchMatchMode.Literal,
            RegexPattern = regex ? "hit" : null };
        var summary = await new TextSearchEngine([extractor]).SearchAsync(request,
            results: new Immediate<FileResult>(results.Add));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        var matches = Assert.Single(results).Matches;
        Assert.Equal(2, matches.Count);
        Assert.Equal([1, 5], matches.Select(match => match.Location.Column));
        Assert.Equal([0, 4], matches.Select(match => match.Offset));
        Assert.All(matches, match =>
        {
            Assert.Equal(7, match.Location.Line);
            Assert.Equal(SourceKind.TextLine, match.Location.Kind);
            Assert.Equal("original-id", match.SourceSegmentId);
            Assert.Equal("hit", match.Text);
        });
        Assert.Equal(" hi", matches[0].After);
        Assert.Equal("it ", matches[1].Before);
        Assert.Equal(originalColumn, source.Column);
        if (originalColumn == 1) Assert.Same(source, matches[0].Location);
        else Assert.NotSame(source, matches[0].Location);
        Assert.NotSame(source, matches[1].Location);
    }

    private sealed class Immediate<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class Segments(IReadOnlyList<TextSegment> segments) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".probe"];
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return segment;
            }
        }
    }
}
