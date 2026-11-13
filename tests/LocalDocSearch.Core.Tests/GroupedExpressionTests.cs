using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class GroupedExpressionTests
{
    public static IEnumerable<object[]> TruthTable() => Enumerable.Range(0, 8).Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(TruthTable))]
    public async Task GroupingChangesBothDisplayedAndExecutedThreeTermTruthTables(int present)
    {
        using var directory = new SearchTestDirectory();
        string[] terms = ["A", "B", "C"];
        var found = Enumerable.Range(0, 3).Select(index => (present & (1 << index)) != 0).ToArray();
        await directory.WriteAsync("truth.txt", string.Join("\n", terms.Where((_, index) => found[index])));
        var right = And(Term(0), Group(Or(Term(1), Term(2))));
        var left = Or(Group(And(Term(0), Term(1))), Term(2));
        Assert.Equal("A AND (B OR C)", SearchFormula.Format(right, terms));
        Assert.Equal("(A AND B) OR C", SearchFormula.Format(left, terms));
        foreach (var (expression, expected) in new[]
                 { (right, found[0] && (found[1] || found[2])), (left, (found[0] && found[1]) || found[2]) })
        {
            Assert.Equal(expected, SearchFormula.Evaluate(expression, found));
            var outcome = await RunAsync(new(directory.Root, terms) { Expression = expression });
            Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
            Assert.Equal(expected ? 1 : 0, outcome.Results.Count);
            Assert.Equal(expected ? found.Count(value => value) : 0, outcome.Summary.Progress.Occurrences);
            Assert.Empty(outcome.Issues);
        }
    }

    [Theory]
    [MemberData(nameof(TruthTable))]
    public void NestedExplicitAndImplicitGroupsHaveEquivalentSemantics(int present)
    {
        var values = Enumerable.Range(0, 3).Select(index => (present & (1 << index)) != 0).ToArray();
        var implicitGroups = And(Term(0), Or(Term(1), Term(2)));
        var explicitGroups = Group(Group(And(Group(Term(0)), Group(Or(Term(1), Group(Term(2)))))));
        Assert.Equal("A AND (B OR C)", SearchFormula.Format(implicitGroups, ["A", "B", "C"]));
        Assert.Equal("(((A) AND (B OR (C))))", SearchFormula.Format(explicitGroups, ["A", "B", "C"]));
        Assert.Equal(SearchFormula.Evaluate(implicitGroups, values), SearchFormula.Evaluate(explicitGroups, values));
    }

    [Fact]
    public void AdjacentApisAndCreatedExpressionsShareFormattingAndEvaluation()
    {
        for (var mask = 0; mask < 4; mask++)
        {
            SearchOperator[] joins = [(mask & 1) == 0 ? SearchOperator.And : SearchOperator.Or,
                (mask & 2) == 0 ? SearchOperator.And : SearchOperator.Or];
            var expression = Assert.IsType<BinaryExpression>(SearchFormula.CreateExpression(3, joins));
            Assert.Equal(SearchFormula.Format(["A", "B", "C"], joins),
                SearchFormula.Format(expression, ["A", "B", "C"]));
            for (var present = 0; present < 8; present++)
            {
                var values = Enumerable.Range(0, 3).Select(index => (present & (1 << index)) != 0).ToArray();
                Assert.Equal(SearchFormula.Evaluate(values, joins), SearchFormula.Evaluate(expression, values));
            }
        }
        Assert.Null(SearchFormula.CreateExpression(0));
        Assert.Null(SearchFormula.CreateExpression(0, []));
        Assert.Equal(new TermExpression(0), SearchFormula.CreateExpression(1, []));
        Assert.Equal("((A))", SearchFormula.Format(Group(Group(Term(0))), ["A"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchFormula.CreateExpression(-1));
        Assert.Throws<ArgumentException>(() => SearchFormula.CreateExpression(0, defaultOperator: (SearchOperator)99));
        Assert.Throws<ArgumentException>(() => SearchFormula.CreateExpression(1, [SearchOperator.And]));
        Assert.Throws<ArgumentException>(() => SearchFormula.CreateExpression(2, [(SearchOperator)99]));
    }

    [Theory]
    [InlineData(false, "A", true, 1)]
    [InlineData(false, "B", false, 0)]
    [InlineData(false, "a A", true, 2)]
    [InlineData(true, "A", false, 0)]
    [InlineData(true, "a", false, 0)]
    [InlineData(true, "A B a", true, 3)]
    public async Task DuplicateLiteralPositionsRemainDistinctWhileMatchingEachLiteralOnce(
        bool matchCase, string text, bool qualifies, int occurrences)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("duplicate.txt", text);
        var request = new SearchRequest(directory.Root, ["A", "B", "a"], MatchCase: matchCase)
            { Expression = And(Term(0), Group(Or(Term(1), Term(2)))) };
        var outcome = await RunAsync(request);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(qualifies ? 1 : 0, outcome.Results.Count);
        Assert.Equal(occurrences, outcome.Summary.Progress.Occurrences);
        if (!matchCase && qualifies)
            Assert.All(Assert.Single(outcome.Results).Matches, match => Assert.Equal("A", match.Term));
        Assert.Empty(outcome.Issues);
    }

    [Fact]
    public async Task LeafIndicesFollowOriginalPositionsEvenWhenTraversalOrderDiffers()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("positions.txt", "B");
        var expression = And(Term(2), Group(Or(Term(0), Term(1))));
        Assert.True(SearchFormula.Evaluate(expression, [true, false, true]));
        Assert.Equal("b AND (B OR missing)", SearchFormula.Format(expression, ["B", "missing", "b"]));
        var outcome = await RunAsync(new(directory.Root, ["B", "missing", "b"]) { Expression = expression });
        Assert.Equal("B", Assert.Single(Assert.Single(outcome.Results).Matches).Term);
        Assert.Equal(1, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData("AND")]
    [InlineData("OR")]
    [InlineData("A AND (B)")]
    [InlineData("(A OR B)")]
    [InlineData("a\"b\\c")]
    public async Task OperatorsParenthesesAndQuotesInsideKeywordsStayLiteral(string term)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("literal.txt", term);
        await directory.WriteAsync("parts.txt", "A\nB");
        var expression = Or(Term(0), Group(Term(1)));
        Assert.Equal(SearchFormula.Format([term]) + " OR (missing)", SearchFormula.Format(expression, [term, "missing"]));
        var outcome = await RunAsync(new(directory.Root, [term, "missing"]) { Expression = expression });
        var result = Assert.Single(outcome.Results);
        Assert.Equal("literal.txt", Path.GetFileName(result.FullPath));
        Assert.Equal(term, Assert.Single(result.Matches).Text);
    }

    [Fact]
    public async Task RequestTermsAndExpressionAreSnapshottedBeforeWorkerCallbacks()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("snapshot.txt", "C");
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var terms = new List<string> { "A", "B", "C" };
        var leaf = Term(2);
        var expression = Or(Group(And(Term(0), Term(1))), leaf);
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new(directory.Root, terms) { Expression = expression },
            progress: new InlineProgress<SearchProgress>(_ =>
            {
                terms.Clear();
                // Deliberately mutate an init-only source through reflection to prove isolation.
                typeof(TermExpression).GetProperty(nameof(TermExpression.TermIndex))!.SetValue(leaf, 99);
            }), results: new InlineProgress<FileResult>(results.Add));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal("C", Assert.Single(Assert.Single(results).Matches).Term);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task GroupedFileQualificationSpansSegmentsWithoutJoiningLiteralText()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segments.txt", "A\nB");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { Expression = And(Term(0), Group(Or(Term(1), Term(2)))) });
        var matches = Assert.Single(outcome.Results).Matches;
        Assert.Equal(new long[] { 1, 2 }, matches.Select(match => match.Location.Line));
        Assert.Equal(2, matches.Select(match => match.SourceSegmentId).Distinct().Count());
        var neverJoined = await RunAsync(new(directory.Root, ["AB", "C"]) { Expression = Or(Group(Term(0)), Term(1)) });
        Assert.Empty(neverJoined.Results);
        Assert.Equal(SearchStatus.Completed, neverJoined.Summary.Status);
    }

    [Fact]
    public async Task TrueOrBranchStillRetainsAllMatchesFromIncompleteBranches()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("all-matches.txt", "A A\nC\nD");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { Expression = Or(Group(And(Term(0), Term(1))), Group(And(Term(2), Term(3)))) });
        Assert.Equal(new[] { "A", "A", "C", "D" }, Assert.Single(outcome.Results).Matches.Select(match => match.Term));
        Assert.Equal(4, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupingRetainsPerFileAndGlobalMatchBudgets(bool global)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("budget.txt", "A A C D");
        var limits = global ? new SearchResourceLimits { MaxTotalMatches = 3 } : new SearchResourceLimits { MaxMatchesPerFile = 3 };
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { Expression = Or(Group(And(Term(0), Term(1))), Group(And(Term(2), Term(3)))) }, limits);
        Assert.Equal(global ? SearchStatus.Limited : SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public async Task UnqualifiedGroupedFileDoesNotConsumeTheGlobalMatchBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("unqualified.txt", "A A A A");
        await directory.WriteAsync("qualified.txt", "C D");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C", "D"])
            { Expression = Or(Group(And(Term(0), Term(1))), Group(And(Term(2), Term(3)))) }, new() { MaxTotalMatches = 2 });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(2, Assert.Single(outcome.Results).TotalOccurrences);
        Assert.Equal(2, outcome.Summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task EarlyTrueGroupStillReadsWholeRealTextAndAppliesLaterSegmentLimits()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("late.txt", "C\n12345");
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { Expression = Or(Group(And(Term(0), Term(1))), Term(2)) }, new() { MaxSegmentCharacters = 4 });
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public async Task CancellationAfterEarlyTrueGroupDiscardsIncompleteRealTextFile()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.txt", "C\nA B");
        using var cancellation = new CancellationTokenSource();
        var extractor = new ObservingTextExtractor(_ => cancellation.Cancel());
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { Expression = Or(Group(And(Term(0), Term(1))), Term(2)) }, extractor: extractor, token: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.ScannedFiles);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public async Task ASourceChangeAfterEarlyTrueGroupDiscardsItsAllMatches()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("changed.txt", "C\nA B");
        var extractor = new ObservingTextExtractor(index =>
        {
            if (index == 0) File.AppendAllText(path, "\nchanged");
        });
        var outcome = await RunAsync(new(directory.Root, ["A", "B", "C"])
            { Expression = Or(Group(And(Term(0), Term(1))), Term(2)) }, extractor: extractor);
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(SearchIssueCode.FileChanged, Assert.Single(outcome.Issues).Code);
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public async Task PreCancellationAndPublicationCancellationKeepExistingCounters()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "C");
        await directory.WriteAsync("two.txt", "C");
        var request = new SearchRequest(directory.Root, ["A", "B", "C"])
            { Expression = Or(Group(And(Term(0), Term(1))), Term(2)) };
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
    public async Task SixtyFourTermLegacyAndGroupedQueriesRemainCompatible()
    {
        using var directory = new SearchTestDirectory();
        var terms = Enumerable.Range(0, 64).Select(index => $"K{index:D3}").ToArray();
        await directory.WriteAsync("many.txt", string.Join(" ", terms));
        Assert.Equal(string.Join(" AND ", terms), SearchFormula.Format(terms));
        var expression = Group(SearchFormula.CreateExpression(64)!);
        var legacy = await RunAsync(new(directory.Root, terms));
        var grouped = await RunAsync(new(directory.Root, terms) { Expression = expression });
        Assert.Equal(SearchStatus.Completed, grouped.Summary.Status);
        Assert.Equal(legacy.Summary.Progress, grouped.Summary.Progress);
        Assert.Equal(64, Assert.Single(grouped.Results).TotalOccurrences);
        Assert.Equal(legacy.Results.SelectMany(result => result.Matches), grouped.Results.SelectMany(result => result.Matches));
    }

    [Fact]
    public void OriginalTermBudgetsApplyToExplicitTreesBeforeDeduplicationAndPathIo()
    {
        var engine = new TextSearchEngine(limits: new() { MaxTerms = 2, MaxTermCharacters = 2 });
        var countError = Assert.Throws<ArgumentException>(() =>
        {
            _ = engine.SearchAsync(new("\0", ["A", "a", "A"]) { Expression = Or(Term(0), And(Term(1), Term(2))) });
        });
        Assert.Contains("MaxTerms", countError.Message);
        var lengthError = Assert.Throws<ArgumentException>(() =>
        {
            _ = engine.SearchAsync(new("\0", ["AAA", "B"]) { Expression = And(Term(0), Term(1)) });
        });
        Assert.Contains("MaxTermCharacters", lengthError.Message);
    }

    [Fact]
    public void ExpressionAndConnectorsAreMutuallyExclusiveAndUniformOperatorIsStillValidated()
    {
        var engine = new TextSearchEngine();
        var ambiguous = Assert.Throws<ArgumentException>(() =>
        {
            _ = engine.SearchAsync(new("\0", ["A"]) { Expression = Term(0), TermConnectors = [] });
        });
        Assert.Contains("Expression", ambiguous.Message);
        Assert.Contains("TermConnectors", ambiguous.Message);
        var invalidOperator = Assert.Throws<ArgumentException>(() =>
        {
            _ = engine.SearchAsync(new("\0", ["A"], (SearchOperator)99) { Expression = Term(0) });
        });
        Assert.Equal("request", invalidOperator.ParamName);
    }

    [Fact]
    public void InvalidNodesChildrenOperatorsAndIndicesAreRejectedByAllEntryPointsBeforePathIo()
    {
        AssertRejectedEverywhere(new UnknownExpression(), 1);
        AssertRejectedEverywhere(new GroupExpression(null!), 1);
        AssertRejectedEverywhere(new BinaryExpression(SearchOperator.And, null!, Term(0)), 1);
        AssertRejectedEverywhere(new BinaryExpression(SearchOperator.Or, Term(0), null!), 1);
        AssertRejectedEverywhere(new BinaryExpression((SearchOperator)99, Term(0), Term(1)), 2);
        AssertRejectedEverywhere(Term(-1), 1);
        AssertRejectedEverywhere(Term(1), 1);
        AssertRejectedEverywhere(Term(int.MaxValue), 1);
        AssertRejectedEverywhere(Term(0), 2);
        var shared = Term(0);
        AssertRejectedEverywhere(And(shared, shared), 1);
        AssertRejectedEverywhere(Or(Term(0), Term(0)), 2);
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Format(null!, ["A"]));
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Evaluate(null!, [true]));
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Format(Term(0), null!));
        Assert.Throws<ArgumentNullException>(() => SearchFormula.Evaluate(Term(0), null!));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format(Term(0), [" " ]));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format(Term(0), []));
        Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate(Term(0), []));
    }

    [Fact]
    public void DepthBoundaryIsInclusiveAndVeryDeepSourcesFailBeforeRecursiveFormatting()
    {
        Assert.Equal(128, SearchFormula.MaxExpressionDepth);
        var maximum = WrapGroups(Term(0), SearchFormula.MaxExpressionDepth - 1);
        Assert.True(SearchFormula.Evaluate(maximum, [true]));
        Assert.Equal(new string('(', 127) + "A" + new string(')', 127), SearchFormula.Format(maximum, ["A"]));
        AssertRejectedEverywhere(Group(maximum), 1, "MaxExpressionDepth");
        AssertRejectedEverywhere(WrapGroups(Term(0), 20_000), 1, "MaxExpressionDepth");
    }

    [Fact]
    public void NodeBoundaryIsInclusiveAndCountsExplicitGroups()
    {
        Assert.Equal(256, SearchFormula.MaxExpressionNodes);
        const int count = 128;
        var balanced = BalancedAnd(0, count); // 128 leaves + 127 binaries.
        var terms = Enumerable.Range(0, count).Select(index => $"K{index:D3}").ToArray();
        var matches = Enumerable.Repeat(true, count).ToArray();
        var maximum = Group(balanced);
        Assert.True(SearchFormula.Evaluate(maximum, matches));
        Assert.Equal("(" + string.Join(" AND ", terms) + ")", SearchFormula.Format(maximum, terms));
        AssertRejectedEverywhere(Group(maximum), count, "MaxExpressionNodes");
        Assert.Throws<ArgumentException>(() => SearchFormula.CreateExpression(129));
        Assert.Throws<ArgumentException>(() => SearchFormula.CreateExpression(int.MaxValue));
        Assert.Throws<ArgumentException>(() => SearchFormula.Format(new string[129]));
        Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate(new bool[129]));
    }

    [Fact]
    public void SharedDagAndCyclesAreBoundedByOccurrencesWithoutStructuralEquality()
    {
        SearchExpression shared = Term(0);
        for (var index = 0; index < 40; index++) shared = And(shared, shared);
        AssertRejectedEverywhere(shared, 1);
        var cyclic = Group(Term(0));
        typeof(GroupExpression).GetProperty(nameof(GroupExpression.Inner))!.SetValue(cyclic, cyclic);
        AssertRejectedEverywhere(cyclic, 1, "MaxExpressionDepth");
    }

    private static void AssertRejectedEverywhere(SearchExpression expression, int termCount, string? reason = null)
    {
        var terms = Enumerable.Range(0, termCount).Select(index => $"K{index:D3}").ToArray();
        var values = new bool[termCount];
        var formatError = Assert.Throws<ArgumentException>(() => SearchFormula.Format(expression, terms));
        var evaluateError = Assert.Throws<ArgumentException>(() => SearchFormula.Evaluate(expression, values));
        var engineError = Assert.Throws<ArgumentException>(() =>
        {
            _ = new TextSearchEngine(limits: new() { MaxTerms = Math.Max(1, termCount) })
                .SearchAsync(new("\0", terms) { Expression = expression });
        });
        Assert.Equal("expression", formatError.ParamName);
        Assert.Equal("expression", evaluateError.ParamName);
        Assert.Equal("expression", engineError.ParamName);
        if (reason is null) return;
        Assert.Contains(reason, formatError.Message);
        Assert.Contains(reason, evaluateError.Message);
        Assert.Contains(reason, engineError.Message);
    }

    private static SearchExpression WrapGroups(SearchExpression expression, int count)
    {
        for (var index = 0; index < count; index++) expression = Group(expression);
        return expression;
    }

    private static SearchExpression BalancedAnd(int start, int count) => count == 1 ? Term(start)
        : And(BalancedAnd(start, count / 2), BalancedAnd(start + count / 2, count - count / 2));

    private static TermExpression Term(int index) => new(index);
    private static BinaryExpression And(SearchExpression left, SearchExpression right) => new(SearchOperator.And, left, right);
    private static BinaryExpression Or(SearchExpression left, SearchExpression right) => new(SearchOperator.Or, left, right);
    private static GroupExpression Group(SearchExpression inner) => new(inner);
    private sealed record UnknownExpression : SearchExpression;

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

    private sealed class ObservingTextExtractor(Action<int> afterSegment) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                var index = 0;
                await foreach (var segment in new PlainTextExtractor().ExtractAsync(path, context, cancellationToken))
                {
                    yield return segment;
                    afterSegment(index++);
                }
            }
            finally { Disposed = true; }
        }
    }
}
