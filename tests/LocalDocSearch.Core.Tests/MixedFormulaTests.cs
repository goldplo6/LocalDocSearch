using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class MixedFormulaTests
{
    public static IEnumerable<object[]> ThreeTermTruthTable()
    {
        for (var joins = 0; joins < 4; joins++)
            for (var present = 0; present < 8; present++)
                yield return [joins, present];
    }

    [Theory]
    [MemberData(nameof(ThreeTermTruthTable))]
    public async Task AllThreeTermTruthTablesMatchAndPrecedence(int joins, int present)
    {
        using var directory = new SearchTestDirectory();
        string[] terms = ["西瓜", "苹果", "荔枝"];
        var matches = Enumerable.Range(0, 3).Select(index => (present & (1 << index)) != 0).ToArray();
        SearchOperator[] connectors = [(joins & 1) == 0 ? SearchOperator.And : SearchOperator.Or,
            (joins & 2) == 0 ? SearchOperator.And : SearchOperator.Or];
        var expected = joins switch
        {
            0 => matches[0] && matches[1] && matches[2],
            1 => matches[0] || (matches[1] && matches[2]),
            2 => (matches[0] && matches[1]) || matches[2],
            _ => matches[0] || matches[1] || matches[2]
        };
        Assert.Equal(expected, SearchFormula.Evaluate(matches, connectors, SearchOperator.Or));
        await directory.WriteAsync("truth.txt", string.Join(" ", terms.Where((_, index) => matches[index])));
        var outcome = await RunAsync(new(directory.Root, terms, SearchOperator.Or) { TermConnectors = connectors });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(expected ? 1 : 0, outcome.Results.Count);
        Assert.Equal(expected ? matches.Count(value => value) : 0, outcome.Summary.Progress.Occurrences);
        Assert.Empty(outcome.Issues);
    }

    [Fact]
    public void FiveTermEvaluatorExhaustivelyChecksEveryConjunctionGroup()
    {
        for (var joins = 0; joins < 16; joins++)
        {
            var connectors = Enumerable.Range(0, 4).Select(index => (joins & (1 << index)) == 0
                ? SearchOperator.And : SearchOperator.Or).ToArray();
            var requiredGroups = new List<int>();
            var required = 1;
            for (var index = 1; index < 5; index++)
            {
                if (connectors[index - 1] == SearchOperator.Or)
                {
                    requiredGroups.Add(required);
                    required = 0;
                }
                required |= 1 << index;
            }
            requiredGroups.Add(required);
            for (var present = 0; present < 32; present++)
            {
                var values = Enumerable.Range(0, 5).Select(index => (present & (1 << index)) != 0).ToArray();
                Assert.Equal(requiredGroups.Any(mask => (present & mask) == mask), SearchFormula.Evaluate(values, connectors));
            }
        }
    }

    [Theory]
    [InlineData(false, "A", true, 1)]
    [InlineData(false, "B", false, 0)]
    [InlineData(false, "a A", true, 2)]
    [InlineData(true, "A", false, 0)]
    [InlineData(true, "a", true, 1)]
    [InlineData(true, "A B a", true, 3)]
    public async Task OriginalDuplicatePositionsRemainInTheExpression(bool matchCase, string text, bool qualifies, int count)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("duplicate.txt", text);
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "a"], MatchCase: matchCase)
            { TermConnectors = [SearchOperator.And, SearchOperator.Or] });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(qualifies ? 1 : 0, outcome.Results.Count);
        Assert.Equal(count, outcome.Summary.Progress.Occurrences);
        if (!matchCase && qualifies) Assert.All(Assert.Single(outcome.Results).Matches, match => Assert.Equal("A", match.Term));
    }

    [Fact]
    public async Task DuplicatePositionsCanAlsoMakeAnAndGroupFail()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("duplicate.txt", "B");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "a"])
            { TermConnectors = [SearchOperator.Or, SearchOperator.And] });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
    }

    [Theory]
    [InlineData(SearchOperator.And)]
    [InlineData(SearchOperator.Or)]
    public async Task NullConnectorsRetainUniformQualificationMatchesAndCounters(SearchOperator operation)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "A A B");
        await directory.WriteAsync("two.txt", "B");
        await directory.WriteAsync("three.txt", "X");
        var request = new SearchRequest(directory.Root, ["A", "B", "a"], operation);
        Assert.Null(request.TermConnectors);
        var uniform = await RunAsync(request);
        var explicitJoins = await RunAsync(request with { TermConnectors = [operation, operation] });
        Assert.Equal(uniform.Summary.Status, explicitJoins.Summary.Status);
        Assert.Equal(uniform.Summary.Progress, explicitJoins.Summary.Progress);
        Assert.Equal(uniform.Results.SelectMany(result => result.Matches), explicitJoins.Results.SelectMany(result => result.Matches));
        Assert.Equal(operation == SearchOperator.And ? 1 : 2, uniform.Results.Count);
        Assert.Equal(operation == SearchOperator.And ? 3 : 4, uniform.Summary.Progress.Occurrences);
    }

    [Fact]
    public async Task FileLevelAndMayQualifyAcrossIndependentSegments()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segments.txt", "西瓜\n苹果");
        var outcome = await RunAsync(new(directory.Root, ["西瓜", "苹果", "荔枝"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or] });
        var matches = Assert.Single(outcome.Results).Matches;
        Assert.Equal(2, matches.Count);
        Assert.Equal(new long[] { 1, 2 }, matches.Select(match => match.Location.Line));
        Assert.Equal(2, matches.Select(match => match.SourceSegmentId).Distinct().Count());
    }

    [Fact]
    public async Task KeywordsAreNeverConstructedAcrossSegments()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segments.txt", "西瓜\n苹果");
        var outcome = await RunAsync(new(directory.Root, ["西瓜苹果", "荔枝", "桃子"])
            { TermConnectors = [SearchOperator.Or, SearchOperator.And] });
        Assert.Empty(outcome.Results);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
    }

    [Fact]
    public async Task OrQualifyingGroupKeepsEveryMatchIncludingOtherIncompleteGroups()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("group.txt", "A A\nC\nD");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or, SearchOperator.And] });
        Assert.Equal(4, Assert.Single(outcome.Results).TotalOccurrences);
        Assert.Equal(new[] { "A", "A", "C", "D" }, outcome.Results[0].Matches.Select(match => match.Term));
        Assert.Equal(4, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData("西瓜AND苹果")]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("西瓜 OR 苹果")]
    [InlineData("(西瓜 AND 苹果)")]
    public async Task OperatorTextInsideAKeywordRemainsLiteral(string term)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("literal.txt", term);
        await directory.WriteAsync("parts.txt", "西瓜\n苹果");
        var outcome = await RunAsync(new(directory.Root, [term, "missing"]) { TermConnectors = [SearchOperator.Or] });
        var result = Assert.Single(outcome.Results);
        Assert.Equal("literal.txt", Path.GetFileName(result.FullPath));
        Assert.Equal(term, Assert.Single(result.Matches).Text);
    }

    [Fact]
    public async Task TermsAndConnectorsAreSnapshottedBeforeTheWorkerStarts()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("snapshot.txt", "C");
        var terms = new List<string> { "A", "B", "C" };
        var connectors = new List<SearchOperator> { SearchOperator.And, SearchOperator.Or };
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new(directory.Root, terms) { TermConnectors = connectors },
            progress: new InlineProgress<SearchProgress>(_ => { terms.Clear(); connectors.Clear(); }),
            results: new InlineProgress<FileResult>(results.Add));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal("C", Assert.Single(Assert.Single(results).Matches).Term);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void InvalidConnectorCountIsRejectedSynchronouslyBeforePathIo(int count)
    {
        var request = new SearchRequest("\0", ["A", "B", "a"])
            { TermConnectors = Enumerable.Repeat(SearchOperator.And, count).ToArray() };
        var error = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(request); });
        Assert.Equal("connectors", error.ParamName);
        Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void InvalidConnectorEnumsAreRejectedBeforePathIo(int invalid)
    {
        var request = new SearchRequest("\0", ["A", "B"]) { TermConnectors = [(SearchOperator)invalid] };
        var error = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(request); });
        Assert.Equal("connectors", error.ParamName);
    }

    [Fact]
    public async Task SingleTermAllowsEmptyConnectorsButNotAnExtraJoin()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("single.txt", "A");
        Assert.Single((await RunAsync(new(directory.Root, ["A"]) { TermConnectors = [] })).Results);
        Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(
            new(directory.Root, ["A"]) { TermConnectors = [SearchOperator.Or] }); });
        Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(
            new(directory.Root, ["A", "B"], (SearchOperator)99) { TermConnectors = [SearchOperator.Or] }); });
    }

    [Fact]
    public void OriginalTermBudgetsStillApplyBeforeDeduplication()
    {
        var engine = new TextSearchEngine(limits: new() { MaxTerms = 2, MaxTermCharacters = 2 });
        Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(
            new("\0", ["A", "a", "A"]) { TermConnectors = [SearchOperator.And, SearchOperator.Or] }); });
        Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(
            new("\0", ["AAA", "B"]) { TermConnectors = [SearchOperator.Or] }); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedQualificationPreservesPerFileAndGlobalMatchBudgets(bool global)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("budget.txt", "A A C D");
        var limits = global ? new SearchResourceLimits { MaxTotalMatches = 3 } : new SearchResourceLimits { MaxMatchesPerFile = 3 };
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or, SearchOperator.And] }, limits);
        Assert.Equal(global ? SearchStatus.Limited : SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public async Task UnqualifiedMixedFileDoesNotConsumeTheGlobalMatchBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("unqualified.txt", "A A A A");
        await directory.WriteAsync("qualified.txt", "C D");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or, SearchOperator.And] }, new() { MaxTotalMatches = 2 });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(2, Assert.Single(outcome.Results).TotalOccurrences);
        Assert.Equal(2, outcome.Summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task AnEarlyTrueOrGroupDoesNotSkipLaterExtractionOrSegmentBudgets()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("late.fake", "fixture");
        var extractor = new SegmentExtractor(["C", "12345"]);
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or] }, new() { MaxSegmentCharacters = 4 }, extractor);
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public async Task CancellationAfterAnOrGroupMatchesDiscardsTheIncompleteFile()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.fake", "fixture");
        using var cancellation = new CancellationTokenSource();
        var extractor = new SegmentExtractor(["C", "A B"], _ => cancellation.Cancel());
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or] }, extractor: extractor, token: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.ScannedFiles);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public async Task PreCancellationAndCancellationAfterPublicationKeepExistingCounters()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "C");
        await directory.WriteAsync("two.txt", "C");
        var request = new SearchRequest(directory.Root, ["A", "B", "C"])
            { TermConnectors = [SearchOperator.And, SearchOperator.Or] };
        using var cancellation = new CancellationTokenSource();
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(request, results: new InlineProgress<FileResult>(result =>
        {
            results.Add(result);
            cancellation.Cancel();
        }), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Single(results);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.Occurrences);
        var preCancelled = await RunAsync(request, token: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, preCancelled.Summary.Status);
        Assert.Equal(0, preCancelled.Summary.Progress.ScannedFiles);
        Assert.Empty(preCancelled.Results);
    }

    [Fact]
    public void FormatterGroupsAndBeforeOrAndShowsLiteralOperatorsUnambiguously()
    {
        Assert.Equal("(西瓜 AND 苹果) OR 荔枝", SearchFormula.Format(["西瓜", "苹果", "荔枝"], [SearchOperator.And, SearchOperator.Or]));
        Assert.Equal("西瓜 OR (苹果 AND 荔枝)", SearchFormula.Format(["西瓜", "苹果", "荔枝"], [SearchOperator.Or, SearchOperator.And]));
        Assert.Equal("(A AND B) OR C OR (D AND E)", SearchFormula.Format(["A", "B", "C", "D", "E"],
            [SearchOperator.And, SearchOperator.Or, SearchOperator.Or, SearchOperator.And]));
        Assert.Equal("A AND B", SearchFormula.Format(["A", "B"]));
        Assert.Equal("A OR B", SearchFormula.Format(["A", "B"], defaultOperator: SearchOperator.Or));
        Assert.Equal("\"西瓜AND苹果\" OR \"or\" OR \"(A)\" OR \"两 个\"", SearchFormula.Format(
            ["西瓜AND苹果", "or", "(A)", "两 个"], defaultOperator: SearchOperator.Or));
        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\u0001\"", SearchFormula.Format(["a\"b\\c\n\r\t\u0001"]));
    }

    [Fact]
    public void SharedHelpersValidateInputsAndHandleEmptyPreviewAndSingleTerm()
    {
        Assert.Equal("", SearchFormula.Format([]));
        Assert.False(SearchFormula.Evaluate([]));
        Assert.Equal("A", SearchFormula.Format(["A"], []));
        Assert.True(SearchFormula.Evaluate([true], []));
        Assert.False(SearchFormula.Evaluate([true, false]));
        Assert.True(SearchFormula.Evaluate([true, false], defaultOperator: SearchOperator.Or));
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Format(null!));
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Evaluate(null!));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format([" "], []));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format(["A", "B"], []));
        Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate([true, false], []));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format(["A", "B"], [(SearchOperator)99]));
        Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate([true, false], [(SearchOperator)99]));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format([], defaultOperator: (SearchOperator)99));
        Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate([], defaultOperator: (SearchOperator)99));
    }

    private static async Task<Outcome> RunAsync(SearchRequest request, SearchResourceLimits? limits = null,
        ITextExtractor? extractor = null, CancellationToken token = default)
    {
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var summary = await new TextSearchEngine(extractor is null ? null : [extractor], limits).SearchAsync(request,
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add), cancellationToken: token);
        return new(summary, results, issues);
    }

    private sealed record Outcome(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues);

    private sealed class SegmentExtractor(string[] segments, Action<int>? afterSegment = null) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Yield();
                for (var index = 0; index < segments.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new($"segment:{index}", segments[index], new(SourceKind.WordParagraph, Paragraph: index + 1));
                    afterSegment?.Invoke(index);
                }
            }
            finally { Disposed = true; }
        }
    }
}
