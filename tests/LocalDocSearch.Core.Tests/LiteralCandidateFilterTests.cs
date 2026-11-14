using System.Runtime.CompilerServices;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class LiteralCandidateFilterTests
{
    public static IEnumerable<object[]> LiteralCases()
    {
        foreach (var count in new[] { 1, 3, 4, 8, 32, 64 })
        foreach (var matchCase in new[] { false, true })
        foreach (var length in new[] { 255, 256, 768 })
        foreach (var corpus in new[] { "ascii", "unicode", "overlap", "absent" })
            yield return [count, matchCase, length, corpus];
    }

    [Theory]
    [MemberData(nameof(LiteralCases))]
    public async Task ResultsEqualIndependentOrdinalReferenceAcrossCandidateAndFallbackPaths(
        int count, bool matchCase, int length, string corpus)
    {
        using var directory = new SearchTestDirectory();
        var (terms, body) = Corpus(count, corpus);
        var text = new string('~', length - body.Length) + body;
        Assert.Equal(length, text.Length);
        await directory.WriteAsync("source.txt", text);
        var request = new SearchRequest(directory.Root, terms, SearchOperator.Or,
            MatchCase: matchCase, ContextCharacters: 7);
        var outcome = await RunAsync(request);
        var expected = Reference([text], terms, matchCase, 7);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Empty(outcome.Issues);
        Assert.Equal(expected.Length, outcome.Summary.Progress.Occurrences);
        if (expected.Length == 0) Assert.Empty(outcome.Files);
        else Assert.Equal(expected, Assert.Single(outcome.Files).Matches.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeduplicationRetainsOriginalTermAndAllLaterOccurrences(bool matchCase)
    {
        using var directory = new SearchTestDirectory();
        var terms = WithMissingCandidates(["apple", "APPLE", "apple", "prefix", "prefix-long", "tail"]);
        var text = new string('~', 256) + "APPLE apple prefix-long tail APPLE prefix apple";
        await directory.WriteAsync("dedupe.txt", text);
        var outcome = await RunAsync(new(directory.Root, terms, SearchOperator.Or,
            MatchCase: matchCase, ContextCharacters: 2));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(Reference([text], terms, matchCase, 2), Assert.Single(outcome.Files).Matches.ToArray());
    }

    [Fact]
    public async Task CandidateSearchNeverJoinsSegmentsAndFileAndStillSpansSegments()
    {
        using var directory = new SearchTestDirectory();
        var lines = new[] { new string('~', 256) + "AB", "CD" + new string('~', 256),
            new string('~', 256) + "left", "right" + new string('~', 256) };
        await directory.WriteAsync("segments.txt", string.Join('\n', lines));
        var terms = WithMissingCandidates(["ABCD", "left", "right", "missing"]);
        SearchExpression expression = new BinaryExpression(SearchOperator.Or, new TermExpression(0),
            new BinaryExpression(SearchOperator.And, new TermExpression(1),
                new BinaryExpression(SearchOperator.Or, new TermExpression(2), new TermExpression(3))));
        for (var index = 4; index < terms.Length; index++)
            expression = new BinaryExpression(SearchOperator.Or, expression, new TermExpression(index));
        var request = new SearchRequest(directory.Root, terms, ContextCharacters: 1) { Expression = expression };
        var outcome = await RunAsync(request);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        var matches = Assert.Single(outcome.Files).Matches;
        Assert.Equal(Reference(lines, terms, false, 1), matches.ToArray());
        Assert.Equal(new[] { "left", "right" }, matches.Select(match => match.Term));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DenseHitsStillEnforcePerFileBudgetsEvenWhenFormulaExcludesFile(
        bool characters, bool qualifies)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("dense.txt", new string('~', 256) + "aaaa");
        var limits = characters ? new SearchResourceLimits { MaxResultCharactersPerFile = 3 }
            : new SearchResourceLimits { MaxMatchesPerFile = 3 };
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["a", "missing", "absent", "never"]),
            qualifies ? SearchOperator.Or : SearchOperator.And, ContextCharacters: 0), limits);
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Files);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal(SearchIssueCode.ResourceLimit, issue.Code);
        Assert.Contains(characters ? "MaxResultCharactersPerFile" : "MaxMatchesPerFile", issue.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExactDenseBudgetsStillAllowQualificationOrNormalExclusion(bool characters, bool qualifies)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("exact.txt", new string('~', 256) + "aaaa");
        var limits = characters ? new SearchResourceLimits { MaxResultCharactersPerFile = 4 }
            : new SearchResourceLimits { MaxMatchesPerFile = 4 };
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["a", "missing", "absent", "never"]),
            qualifies ? SearchOperator.Or : SearchOperator.And, ContextCharacters: 0), limits);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Empty(outcome.Issues);
        Assert.Equal(qualifies ? 4 : 0, outcome.Summary.Progress.Occurrences);
        if (qualifies) Assert.Equal(4, Assert.Single(outcome.Files).TotalOccurrences);
        else Assert.Empty(outcome.Files);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    public async Task NoCandidateSegmentStillEnforcesLengthBudgetBeforeFiltering(int length)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segment.txt", new string('~', length));
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["needle", "absent", "missing", "never"]), SearchOperator.Or),
            new() { MaxSegmentCharacters = 255 });
        Assert.Empty(outcome.Files);
        Assert.Equal(length == 255 ? SearchStatus.Completed : SearchStatus.Failed, outcome.Summary.Status);
        if (length == 255) Assert.Empty(outcome.Issues);
        else Assert.Contains("MaxSegmentCharacters", Assert.Single(outcome.Issues).Message);
    }

    [Theory]
    [InlineData(1, 1, "MaxMatchesPerFile")]
    [InlineData(2, 1, "MaxResultCharactersPerFile")]
    public async Task OriginalMatchBeforeCharacterBudgetPriorityIsPreserved(int matches, int characters, string limit)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("priority.txt", new string('~', 256) + "aa");
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["a", "missing", "absent", "never"]),
            ContextCharacters: 0), new() { MaxMatchesPerFile = matches, MaxResultCharactersPerFile = characters });
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Contains(limit, Assert.Single(outcome.Issues).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateMissOrEarlyMatchStillReadsStrictDecoderThroughEof(bool earlyMatch)
    {
        using var directory = new SearchTestDirectory();
        var prefix = new string('~', 32768) + (earlyMatch ? "needle" : "") + "\n";
        await directory.WriteBytesAsync("late.txt", Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xc3 }).ToArray());
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["needle", "absent", "missing", "never"]), SearchOperator.Or));
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Files);
        Assert.Equal(SearchIssueCode.Encoding, Assert.Single(outcome.Issues).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CandidateMissStillVisitsLateWarningAndFailureAndDisposesIterator(bool fail)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("late.fake", "fixture");
        var extractor = new LateExtractor(fail);
        var outcome = await RunAsync(new(directory.Root, WithMissingCandidates(["needle", "absent", "missing", "never"]), SearchOperator.Or),
            extractor: extractor);
        Assert.True(extractor.FinishedFirstSegment);
        Assert.True(extractor.Disposed);
        Assert.Empty(outcome.Files);
        Assert.Equal(fail ? SearchStatus.Failed : SearchStatus.Partial, outcome.Summary.Status);
        Assert.Equal(1, outcome.Summary.Diagnostics.WarningCount);
        Assert.Contains(outcome.Issues, issue => issue.Message == "late warning");
        if (fail) Assert.Contains(outcome.Issues, issue => issue.Code == SearchIssueCode.InvalidData);
        else Assert.Single(outcome.Issues);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task SharedCandidateSnapshotPreservesConcurrentContentAndDiscoveryOrder(int width, bool matchCase)
    {
        using var directory = new SearchTestDirectory();
        var terms = WithMissingCandidates(["aa", "aaa", "西瓜", "σ", "k", "i", "😀", "tail"], 64);
        for (var index = 0; index < 9; index++)
            await directory.WriteAsync($"{index}.txt", new string('~', 256 + index) + "aaaaa 西瓜 Σ K I 😀 tail\n" + new string('~', 256));
        var request = new SearchRequest(directory.Root, terms, SearchOperator.Or,
            MatchCase: matchCase, ContextCharacters: 3);
        var sequential = await RunAsync(request);
        var parallel = await RunAsync(request, width: width);
        Assert.Equal(SearchStatus.Completed, sequential.Summary.Status);
        Assert.Equal(SearchStatus.Completed, parallel.Summary.Status);
        Assert.Equal(sequential.Files.Select(file => file.FullPath), parallel.Files.Select(file => file.FullPath));
        Assert.Equal(sequential.Summary.Progress, parallel.Summary.Progress);
        Assert.Empty(parallel.Issues);
        for (var index = 0; index < sequential.Files.Count; index++)
            Assert.Equal(sequential.Files[index].Matches.ToArray(), parallel.Files[index].Matches.ToArray());
    }

    private static (string[] Terms, string Body) Corpus(int count, string corpus)
    {
        string[] seeds = corpus switch
        {
            "unicode" => ["西瓜", "ß", "σ", "k", "i", "😀", "İ", "ı"],
            "overlap" => ["aa", "aaa", "a", "aaaa"],
            _ => ["needle", "prefix", "prefix-long", "tail"]
        };
        var terms = seeds.Take(count).Concat(Enumerable.Range(seeds.Length, Math.Max(0, count - seeds.Length))
            .Select(index => $"token{index:D3}")).ToArray();
        var body = corpus switch
        {
            "unicode" => "西瓜 SS ß ẞ σ ς Σ K K i I İ ı 😀 西瓜 σ",
            "overlap" => "aaaaa aa aaaaa",
            "absent" => "completely unrelated text",
            _ => "prefix-long NEEDLE needle tail prefix needle"
        };
        if (count >= 32 && corpus != "absent") body += $" token{count - 1:D3} token{count - 1:D3}";
        return (terms, body);
    }

    // Preserve seed order/duplicates and ensure both ordinal modes exercise candidate filtering.
    // Padding literals never occur in these synthetic sources or influence the expected matches.
    private static string[] WithMissingCandidates(string[] terms, int distinctCount = 32) =>
        terms.Concat(Enumerable.Range(0, distinctCount - terms.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .Select(index => $"missing-candidate-{index:D3}")).ToArray();

    // Deliberately independent of SearchValues and the engine's candidate offset. Search each
    // distinct literal from zero, advancing by its own length, then order actual source hits.
    private static MatchItem[] Reference(IReadOnlyList<string> segments, IReadOnlyList<string> terms,
        bool matchCase, int context)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var comparer = matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var output = new List<MatchItem>();
        for (var segment = 0; segment < segments.Count; segment++)
        {
            var text = segments[segment];
            var hits = new List<MatchItem>();
            foreach (var term in terms.Distinct(comparer))
            {
                var offset = text.IndexOf(term, comparison);
                while (offset >= 0)
                {
                    var end = offset + term.Length;
                    hits.Add(new MatchItem(term, text.Substring(Math.Max(0, offset - context), Math.Min(context, offset)),
                        text.Substring(offset, term.Length), text.Substring(end, Math.Min(context, text.Length - end)),
                        new SourceLocation(segment + 1, offset + 1), $"line:{segment + 1}", offset));
                    offset = text.IndexOf(term, end, comparison);
                }
            }
            output.AddRange(hits.OrderBy(hit => hit.Offset).ThenBy(hit => hit.Term, StringComparer.Ordinal));
        }
        return output.ToArray();
    }

    private static async Task<Outcome> RunAsync(SearchRequest request, SearchResourceLimits? limits = null,
        ITextExtractor? extractor = null, int width = 1)
    {
        var files = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var engine = new TextSearchEngine(extractor is null ? null : [extractor], limits);
        var summary = await engine.SearchAsync(request, results: new InlineProgress<FileResult>(files.Add),
            issues: new InlineProgress<SearchIssue>(issues.Add), maxConcurrency: width);
        return new(summary, files, issues);
    }

    private sealed record Outcome(SearchSummary Summary, List<FileResult> Files, List<SearchIssue> Issues);

    private sealed class LateExtractor(bool fail) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public bool FinishedFirstSegment { get; private set; }
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TextSegment("first", new string('~', 256), new SourceLocation(1, 1));
                FinishedFirstSegment = true;
                context.Warn("late warning");
                if (fail) throw new ExtractionException("late corruption");
            }
            finally { Disposed = true; }
        }
    }
}
