using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class ResultCharacterBudgetTests
{
    [Fact]
    public void DefaultsBoundRetainedUtf16PayloadAndAreSnapshotted()
    {
        var limits = new SearchResourceLimits();
        Assert.Equal(16_777_216, limits.MaxResultCharactersPerFile);
        Assert.Equal(33_554_432, limits.MaxTotalResultCharacters);
        var context = new ExtractionContext(_ => { }, limits: limits);
        Assert.NotSame(limits, context.Limits);
        Assert.Equal(limits, context.Limits);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void NewBudgetsRejectInvalidValuesAtBothEntryPoints(bool global, int value)
    {
        var limits = global ? new SearchResourceLimits { MaxTotalResultCharacters = value }
            : new SearchResourceLimits { MaxResultCharactersPerFile = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSearchEngine(limits: limits));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractionContext(_ => { }, limits: limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerFileBoundaryIncludesBothContextsAndDropsWholeOverBudgetFile(bool regex)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("large.txt", "xxaYY");
        await directory.WriteAsync("small.txt", "a");
        var request = Request(directory.Root, regex, context: 500);
        var exact = await RunAsync(request, new() { MaxResultCharactersPerFile = 5 });
        Assert.Equal(SearchStatus.Completed, exact.Summary.Status);
        Assert.Equal(2, exact.Files.Count);
        Assert.Equal(6, Characters(exact.Files));
        var exceeded = await RunAsync(request, new() { MaxResultCharactersPerFile = 4 });
        Assert.Equal(SearchStatus.Partial, exceeded.Summary.Status);
        Assert.Equal("small.txt", Path.GetFileName(Assert.Single(exceeded.Files).FullPath));
        var issue = Assert.Single(exceeded.Issues);
        Assert.Equal(SearchIssueCode.ResourceLimit, issue.Code);
        Assert.Contains("MaxResultCharactersPerFile", issue.Message);
        Assert.Equal(1, exceeded.Summary.Diagnostics.ErrorCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalBoundaryRetainsOnlyCompleteFiles(bool regex)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "xab尾");
        await directory.WriteAsync("two.txt", "xab尾");
        var request = regex ? Request(directory.Root, true, 1) with { RegexPattern = "ab" }
            : new SearchRequest(directory.Root, ["ab"], ContextCharacters: 1);
        var exact = await RunAsync(request, new() { MaxTotalResultCharacters = 8 });
        Assert.Equal(SearchStatus.Completed, exact.Summary.Status);
        Assert.Equal(2, exact.Files.Count);
        Assert.Equal(8, Characters(exact.Files));
        var exceeded = await RunAsync(request, new() { MaxTotalResultCharacters = 7 });
        Assert.Equal(SearchStatus.Limited, exceeded.Summary.Status);
        var file = Assert.Single(exceeded.Files);
        var match = Assert.Single(file.Matches);
        Assert.Equal(("x", "ab", "尾"), (match.Before, match.Text, match.After));
        Assert.Equal(4, Characters(exceeded.Files));
        Assert.Contains("MaxTotalResultCharacters=7", exceeded.Summary.Diagnostics.LimitReason);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(exceeded.Issues).Code);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public async Task BudgetCountsUtf16UnitsIncludingSurrogatePairs(int limit, bool accepted)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("unicode.txt", "😀a😀");
        var result = await RunAsync(Request(directory.Root, false, 500), new() { MaxResultCharactersPerFile = limit });
        Assert.Equal(accepted ? SearchStatus.Completed : SearchStatus.Failed, result.Summary.Status);
        if (accepted) Assert.Equal(5, Characters(result.Files));
        else Assert.Empty(result.Files);
    }

    [Fact]
    public async Task NonQualifyingFileDoesNotConsumeGlobalCharacters()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("unqualified.txt", "a");
        await directory.WriteAsync("qualified.txt", "xabY");
        var request = new SearchRequest(directory.Root, ["a", "b"], ContextCharacters: 1);
        var result = await RunAsync(request, new() { MaxTotalResultCharacters = 6 });
        Assert.Equal(SearchStatus.Completed, result.Summary.Status);
        Assert.Equal("qualified.txt", Path.GetFileName(Assert.Single(result.Files).FullPath));
        Assert.Equal(6, Characters(result.Files));
    }

    [Fact]
    public async Task LongRegexMatchIsRejectedWithoutPublishingItsValue()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("long.txt", new string('a', 100_000));
        var request = Request(directory.Root, true, 0) with { RegexPattern = "a+" };
        var result = await RunAsync(request, new() { MaxResultCharactersPerFile = 1_000 });
        Assert.Equal(SearchStatus.Failed, result.Summary.Status);
        Assert.Empty(result.Files);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SearchIssueCode.ResourceLimit, issue.Code);
        Assert.DoesNotContain(new string('a', 100), issue.Message);
        Assert.DoesNotContain("a+", issue.Message);
    }

    [Fact]
    public async Task RepeatedSearchHasIndependentGlobalBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "a");
        var engine = new TextSearchEngine(limits: new() { MaxTotalResultCharacters = 1 });
        for (var run = 0; run < 3; run++)
        {
            var files = new List<FileResult>();
            var summary = await engine.SearchAsync(new(directory.Root, ["a"], ContextCharacters: 0),
                results: new InlineProgress<FileResult>(files.Add));
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Equal(1, Characters(files));
        }
    }

    [Fact]
    public async Task ReusedSegmentScratchPreservesOrderingAndDoesNotRepeatOldMatches()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segments.txt", "b a\nnone\na b");
        var result = await RunAsync(new(directory.Root, ["a", "b"], ContextCharacters: 0), new());
        var matches = Assert.Single(result.Files).Matches;
        Assert.Equal(new[] { "b", "a", "a", "b" }, matches.Select(m => m.Text));
        Assert.Equal(new long[] { 1, 1, 3, 3 }, matches.Select(m => m.Location.Line));
        Assert.Equal(new[] { 0, 2, 0, 2 }, matches.Select(m => m.Offset));
    }

    private static SearchRequest Request(string root, bool regex, int context) => regex
        ? new(root, [], ContextCharacters: context) { MatchMode = SearchMatchMode.RegularExpression, RegexPattern = "a" }
        : new(root, ["a"], ContextCharacters: context);

    private static int Characters(IEnumerable<FileResult> files) => files.Sum(f => f.Matches.Sum(m => m.Before.Length + m.Text.Length + m.After.Length));

    private static async Task<Outcome> RunAsync(SearchRequest request, SearchResourceLimits limits)
    {
        var files = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var summary = await new TextSearchEngine(limits: limits).SearchAsync(request,
            results: new InlineProgress<FileResult>(files.Add), issues: new InlineProgress<SearchIssue>(issues.Add));
        return new(summary, files, issues);
    }

    private sealed record Outcome(SearchSummary Summary, List<FileResult> Files, List<SearchIssue> Issues);
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
