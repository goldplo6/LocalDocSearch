using System.Runtime.CompilerServices;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class ResourceLimitTests
{
    [Fact]
    public void DefaultsAndExistingEnumValuesRemainStable()
    {
        var limits = new SearchResourceLimits();
        Assert.Equal(1_048_576, limits.MaxSegmentCharacters);
        Assert.Equal(50_000, limits.MaxMatchesPerFile);
        Assert.Equal(200_000, limits.MaxTotalMatches);
        Assert.Equal(20_000, limits.MaxMatchedFiles);
        Assert.Equal(100_000, limits.MaxScannedFiles);
        Assert.Equal(1_000, limits.MaxIssueDetails);
        Assert.Equal(100, limits.MaxWarningsPerFile);
        Assert.Equal(20_000, limits.MaxPendingDirectories);
        Assert.Equal(128, limits.MaxDirectoryDepth);
        Assert.Equal(64, limits.MaxTerms);
        Assert.Equal(4096, limits.MaxTermCharacters);
        Assert.Equal(0, (int)SearchStatus.Completed);
        Assert.Equal(1, (int)SearchStatus.Cancelled);
        Assert.Equal(2, (int)SearchStatus.Partial);
        Assert.Equal(3, (int)SearchStatus.Failed);
        Assert.Equal(4, (int)SearchStatus.Limited);
        Assert.Equal(SearchIssueCode.Unknown, new SearchIssue("path", "message").Code);
        Assert.NotNull(new SearchSummary(SearchStatus.Completed, new(0, 0, 0, 0, null)).Diagnostics);
    }

    [Fact]
    public void EveryBudgetRejectsZeroAndNegativeValuesAtBothEntryPoints()
    {
        foreach (var value in new[] { 0, -1 })
        {
            var invalid = new[]
            {
                new SearchResourceLimits { MaxSegmentCharacters = value },
                new SearchResourceLimits { MaxMatchesPerFile = value },
                new SearchResourceLimits { MaxTotalMatches = value },
                new SearchResourceLimits { MaxMatchedFiles = value },
                new SearchResourceLimits { MaxScannedFiles = value },
                new SearchResourceLimits { MaxIssueDetails = value },
                new SearchResourceLimits { MaxWarningsPerFile = value },
                new SearchResourceLimits { MaxPendingDirectories = value },
                new SearchResourceLimits { MaxDirectoryDepth = value },
                new SearchResourceLimits { MaxTerms = value },
                new SearchResourceLimits { MaxTermCharacters = value }
            };
            foreach (var limits in invalid)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new TextSearchEngine(limits: limits));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractionContext(_ => { }, limits: limits));
            }
        }
    }

    [Fact]
    public void ContextTakesAnImmutableValidatedSnapshot()
    {
        var limits = new SearchResourceLimits { MaxSegmentCharacters = 7 };
        var context = new ExtractionContext(_ => { }, limits: limits);
        Assert.NotSame(limits, context.Limits);
        Assert.Equal(limits, context.Limits);
        var changed = limits with { MaxSegmentCharacters = 9 };
        Assert.Equal(7, context.Limits.MaxSegmentCharacters);
        Assert.Equal(9, changed.MaxSegmentCharacters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task ContextCharacterEndpointsAreAccepted(int count)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("context.txt", "xa尾");
        var outcome = await RunAsync(directory, new(), new(directory.Root, ["a"], ContextCharacters: count));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        var match = Assert.Single(Assert.Single(outcome.Results).Matches);
        Assert.Equal(count == 0 ? "" : "x", match.Before);
        Assert.Equal(count == 0 ? "" : "尾", match.After);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(int.MaxValue)]
    public void ContextCharactersOutsideTheRangeAreRejectedSynchronously(int count)
    {
        using var directory = new SearchTestDirectory();
        Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(new(directory.Root, ["a"], ContextCharacters: count)); });
    }

    [Fact]
    public async Task TermBudgetsApplyBeforeDeduplicationAndMeasureUtf16()
    {
        using var directory = new SearchTestDirectory();
        var engine = new TextSearchEngine(limits: new() { MaxTerms = 2, MaxTermCharacters = 2 });
        Assert.Equal(SearchStatus.Completed, (await engine.SearchAsync(new(directory.Root, ["😀", "aa"]))).Status);
        Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(new(directory.Root, ["a", "a", "a"])); });
        Assert.Throws<ArgumentException>(() => { _ = engine.SearchAsync(new(directory.Root, ["😀a"])); });
        var defaults = new TextSearchEngine();
        Assert.Equal(SearchStatus.Completed, (await defaults.SearchAsync(new(directory.Root, Enumerable.Repeat("a", 64).ToArray()))).Status);
        Assert.Throws<ArgumentException>(() => { _ = defaults.SearchAsync(new(directory.Root, Enumerable.Repeat("a", 65).ToArray())); });
        Assert.Equal(SearchStatus.Completed, (await defaults.SearchAsync(new(directory.Root, [new string('a', 4096)]))).Status);
        Assert.Throws<ArgumentException>(() => { _ = defaults.SearchAsync(new(directory.Root, [new string('a', 4097)])); });
    }

    [Fact]
    public async Task QuerySnapshotsTermsAndExtensionsBeforeBackgroundSearch()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("snapshot.txt", "a");
        var terms = new List<string> { "a" };
        var extensions = new List<string> { "txt" };
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new(directory.Root, terms, Extensions: extensions),
            progress: new InlineProgress<SearchProgress>(_ => { terms[0] = "missing"; extensions.Clear(); }),
            results: new InlineProgress<FileResult>(results.Add));
        Assert.Equal(SearchStatus.Completed, summary.Status);
        Assert.Equal("a", Assert.Single(Assert.Single(results).Matches).Term);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public async Task ExactSegmentLimitCompletesAndAdditionalCharacterFailsTheEntireFile(string extension)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("exact" + extension, "aaaa");
        var limits = new SearchResourceLimits { MaxSegmentCharacters = 4 };
        var exact = await RunAsync(directory, limits);
        Assert.Equal(SearchStatus.Completed, exact.Summary.Status);
        Assert.Equal(4, Assert.Single(exact.Results).TotalOccurrences);
        await directory.WriteAsync("exact" + extension, "a\nxxxxx");
        await directory.WriteAsync("survivor.txt", "a");
        var exceeded = await RunAsync(directory, limits);
        Assert.Equal(SearchStatus.Partial, exceeded.Summary.Status);
        Assert.Equal("survivor.txt", Path.GetFileName(Assert.Single(exceeded.Results).FullPath));
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(exceeded.Issues).Code);
        Assert.Equal(2, exceeded.Summary.Progress.ScannedFiles);
        Assert.Equal(1, exceeded.Summary.Diagnostics.ErrorCount);
    }

    [Fact]
    public async Task EngineAlsoChecksSegmentsFromCustomExtractors()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("custom.fake", "fixture");
        var extractor = new FixtureExtractor(["a", "12345"]);
        var outcome = await RunAsync(directory, new() { MaxSegmentCharacters = 4 }, extractor: extractor);
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
        Assert.Equal(4, extractor.SeenLimits!.MaxSegmentCharacters);
    }

    [Theory]
    [InlineData(2, SearchStatus.Completed, 2)]
    [InlineData(3, SearchStatus.Failed, 0)]
    public async Task PerFileMatchBudgetDistinguishesExactFromOneExtra(int occurrences, SearchStatus status, int emitted)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("matches.txt", new string('a', occurrences));
        var outcome = await RunAsync(directory, new() { MaxMatchesPerFile = 2 });
        Assert.Equal(status, outcome.Summary.Status);
        Assert.Equal(emitted, outcome.Summary.Progress.Occurrences);
        Assert.Equal(emitted == 0 ? 0 : 1, outcome.Results.Count);
        if (emitted == 0) Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(outcome.Issues).Code);
    }

    [Fact]
    public async Task LatePerFileMatchLimitDiscardsEarlierSegmentsAndContinuesSurvivors()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("exceeded.txt", "a\na\na");
        await directory.WriteAsync("survivor.txt", "a");
        var outcome = await RunAsync(directory, new() { MaxMatchesPerFile = 2 });
        Assert.Equal(SearchStatus.Partial, outcome.Summary.Status);
        Assert.Equal("survivor.txt", Path.GetFileName(Assert.Single(outcome.Results).FullPath));
        Assert.Equal(1, outcome.Summary.Progress.Occurrences);
        Assert.Equal(1, outcome.Summary.Progress.SkippedFiles);
    }

    [Fact]
    public async Task AndMissingTermNeverPublishesAnOverBudgetPartialFile()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("missing.txt", "aaa");
        var outcome = await RunAsync(directory, new() { MaxMatchesPerFile = 2 }, new(directory.Root, ["a", "z"]));
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalMatchBudgetPublishesOnlyWholeFilesAndStopsOnlyOnAnExtraQualifiedFile(bool extra)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "aa");
        await directory.WriteAsync("two.txt", "aa");
        if (extra) await directory.WriteAsync("three.txt", "aa");
        var outcome = await RunAsync(directory, new() { MaxTotalMatches = 4 });
        Assert.Equal(extra ? SearchStatus.Limited : SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(2, outcome.Results.Count);
        Assert.All(outcome.Results, result => Assert.Equal(2, result.TotalOccurrences));
        Assert.Equal(4, outcome.Summary.Progress.Occurrences);
        if (extra) Assert.Contains("MaxTotalMatches=4", outcome.Summary.Diagnostics.LimitReason);
        else Assert.Null(outcome.Summary.Diagnostics.LimitReason);
    }

    [Fact]
    public async Task GlobalBudgetRejectsAnEntireFirstFileRatherThanItsPrefix()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("whole.txt", "aaa");
        var outcome = await RunAsync(directory, new() { MaxTotalMatches = 2 });
        Assert.Equal(SearchStatus.Limited, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.Equal(1, outcome.Summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task AnAndUnqualifiedFileDoesNotSpendTheGlobalPublishedMatchBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("missing.txt", "aaaa");
        await directory.WriteAsync("qualified.txt", "az");
        var outcome = await RunAsync(directory, new() { MaxTotalMatches = 2 }, new(directory.Root, ["a", "z"]));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal("qualified.txt", Path.GetFileName(Assert.Single(outcome.Results).FullPath));
        Assert.Equal(2, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchedFileBudgetCompletesAtExactAndLimitsOneExtra(bool extra)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "a");
        if (extra) await directory.WriteAsync("two.txt", "a");
        var outcome = await RunAsync(directory, new() { MaxMatchedFiles = 1 });
        Assert.Equal(extra ? SearchStatus.Limited : SearchStatus.Completed, outcome.Summary.Status);
        Assert.Single(outcome.Results);
        if (extra) Assert.Contains("MaxMatchedFiles=1", outcome.Summary.Diagnostics.LimitReason);
    }

    [Fact]
    public async Task ReachingMatchedFileBudgetDoesNotPreventFinishingUnmatchedFiles()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "a");
        await directory.WriteAsync("unmatched.txt", "z");
        var outcome = await RunAsync(directory, new() { MaxMatchedFiles = 1, MaxTotalMatches = 1 });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(2, outcome.Summary.Progress.ScannedFiles);
        Assert.Single(outcome.Results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScannedFileBudgetCountsOnlyEligibleFilesAndLimitsBeforeOpeningExtra(bool extra)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("eligible.txt", "a");
        await directory.WriteBytesAsync("ignored.bin", [0xff]);
        await directory.WriteBytesAsync("~$ignored.txt", [0xff]);
        if (extra) await directory.WriteAsync("extra.txt", "a");
        var outcome = await RunAsync(directory, new() { MaxScannedFiles = 1 });
        Assert.Equal(extra ? SearchStatus.Limited : SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(1, outcome.Summary.Progress.ScannedFiles);
        Assert.Single(outcome.Results);
        if (extra) Assert.Contains("MaxScannedFiles=1", outcome.Summary.Diagnostics.LimitReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryDepthTreatsRootAsZeroAndReportsAnExtraChild(bool extra)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync(Path.Combine("child", "one.txt"), "a");
        if (extra) Directory.CreateDirectory(Path.Combine(directory.Root, "child", "grandchild"));
        var outcome = await RunAsync(directory, new() { MaxDirectoryDepth = 1 });
        Assert.Equal(extra ? SearchStatus.Limited : SearchStatus.Completed, outcome.Summary.Status);
        if (extra) Assert.Contains("MaxDirectoryDepth=1", outcome.Summary.Diagnostics.LimitReason);
        else Assert.Single(outcome.Results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDirectoryBudgetChecksBeforeGrowingTheFrontier(bool extra)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync(Path.Combine("child1", "one.txt"), "a");
        if (extra) Directory.CreateDirectory(Path.Combine(directory.Root, "child2"));
        var outcome = await RunAsync(directory, new() { MaxPendingDirectories = 1 });
        Assert.Equal(extra ? SearchStatus.Limited : SearchStatus.Completed, outcome.Summary.Status);
        if (extra)
        {
            Assert.Empty(outcome.Results);
            Assert.Contains("MaxPendingDirectories=1", outcome.Summary.Diagnostics.LimitReason);
        }
        else Assert.Single(outcome.Results);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(10_000, 9998)]
    public async Task PerFileWarningsAreBoundedAndAllOriginalWarningsAreCounted(int count, long suppressed)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("warnings.fake", "fixture");
        var outcome = await RunAsync(directory, new() { MaxWarningsPerFile = 2 }, extractor: new FixtureExtractor(["a"], count));
        Assert.Equal(SearchStatus.Partial, outcome.Summary.Status);
        Assert.Equal(count, outcome.Summary.Diagnostics.WarningCount);
        Assert.Equal(0, outcome.Summary.Diagnostics.ErrorCount);
        Assert.Equal(suppressed, outcome.Summary.Diagnostics.SuppressedIssueCount);
        Assert.Equal(suppressed == 0 ? 2 : 3, outcome.Issues.Count);
        var fileWarnings = Assert.Single(outcome.Results).ExtractionWarnings;
        Assert.Equal(suppressed == 0 ? 2 : 3, fileWarnings.Count);
        if (suppressed != 0)
        {
            Assert.Contains("聚合", fileWarnings[^1]);
            Assert.Contains("限额外唯一聚合", outcome.Issues[^1].Message);
        }
    }

    [Fact]
    public async Task GlobalAndPerFileIssueCapsDoNotDoubleCountSuppressedWarnings()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        await directory.WriteAsync("two.fake", "fixture");
        var outcome = await RunAsync(directory, new() { MaxIssueDetails = 1, MaxWarningsPerFile = 2 },
            extractor: new FixtureExtractor(["a"], 5));
        Assert.Equal(10, outcome.Summary.Diagnostics.WarningCount);
        Assert.Equal(9, outcome.Summary.Diagnostics.SuppressedIssueCount);
        Assert.Equal(2, outcome.Issues.Count);
        Assert.Equal(2, outcome.Results.Count);
        Assert.All(outcome.Results, result => Assert.Equal(3, result.ExtractionWarnings.Count));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    public async Task IssueDetailCapRetainsCompleteErrorsEvenWhenDetailsAreOmitted(int files, long suppressed)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < files; index++) await directory.WriteBytesAsync($"bad-{index}.txt", [0xff]);
        var outcome = await RunAsync(directory, new() { MaxIssueDetails = 1 });
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Equal(files, outcome.Summary.Diagnostics.ErrorCount);
        Assert.Equal(0, outcome.Summary.Diagnostics.WarningCount);
        Assert.Equal(suppressed, outcome.Summary.Diagnostics.SuppressedIssueCount);
        Assert.Equal(suppressed == 0 ? 1 : 2, outcome.Issues.Count);
        Assert.Equal(SearchIssueCode.Encoding, outcome.Issues[0].Code);
        Assert.True(outcome.Summary.Diagnostics.ElapsedMilliseconds >= 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceLengthOrMtimeDiscardsAllMatchesAndDoesNotStopOtherFiles(bool sameLength)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("changed.fake", "fixture");
        await directory.WriteAsync("survivor.fake", "fixture");
        var outcome = await RunAsync(directory, new(), extractor: new FixtureExtractor(["a"], afterSegments: async path =>
        {
            if (!Path.GetFileName(path).StartsWith("changed", StringComparison.Ordinal)) return;
            if (sameLength) File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
            else await File.AppendAllTextAsync(path, "changed");
        }));
        Assert.Equal(SearchStatus.Partial, outcome.Summary.Status);
        Assert.Equal("survivor.fake", Path.GetFileName(Assert.Single(outcome.Results).FullPath));
        Assert.Equal(SearchIssueCode.FileChanged, Assert.Single(outcome.Issues).Code);
        Assert.Equal(1, outcome.Summary.Diagnostics.ErrorCount);
    }

    [Fact]
    public async Task CancellationDuringWarningFloodStopsAndDisposesTheExtractor()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.fake", "fixture");
        using var cancellation = new CancellationTokenSource();
        var extractor = new FixtureExtractor(["a"], 100_000);
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine([extractor], new() { MaxWarningsPerFile = 2 }).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Add),
            issues: new InlineProgress<SearchIssue>(_ => cancellation.Cancel()), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Empty(results);
        Assert.True(extractor.Disposed);
        Assert.Equal(0, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Diagnostics.WarningCount);
    }

    [Fact]
    public async Task ThrottledProgressStillAllowsCancellationAtTheFirstPublishedMatch()
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < 5; index++) await directory.WriteAsync($"file-{index}.txt", "a");
        using var cancellation = new CancellationTokenSource();
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(new(directory.Root, ["a"]),
            progress: new InlineProgress<SearchProgress>(value =>
            {
                if (value.MatchedFiles == 1) cancellation.Cancel();
            }), results: new InlineProgress<FileResult>(results.Add), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Single(results);
        Assert.Equal(1, summary.Progress.ScannedFiles);
        Assert.Equal(1, summary.Progress.MatchedFiles);
    }

    [Fact]
    public async Task FailedFilesAlsoSpendTheScannedFileBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteBytesAsync("bad1.txt", [0xff]);
        await directory.WriteBytesAsync("bad2.txt", [0xff]);
        var outcome = await RunAsync(directory, new() { MaxScannedFiles = 1 });
        Assert.Equal(SearchStatus.Limited, outcome.Summary.Status);
        Assert.Equal(1, outcome.Summary.Progress.ScannedFiles);
        Assert.Equal(1, outcome.Summary.Progress.SkippedFiles);
        Assert.Empty(outcome.Results);
        Assert.Equal(2, outcome.Summary.Diagnostics.ErrorCount);
        Assert.Contains("MaxScannedFiles=1", outcome.Summary.Diagnostics.LimitReason);
    }

    private static async Task<Outcome> RunAsync(SearchTestDirectory directory, SearchResourceLimits limits,
        SearchRequest? request = null, ITextExtractor? extractor = null)
    {
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var progress = new List<SearchProgress>();
        var summary = await new TextSearchEngine(extractor is null ? null : [extractor], limits).SearchAsync(
            request ?? new(directory.Root, ["a"]), new InlineProgress<SearchProgress>(progress.Add),
            new InlineProgress<FileResult>(results.Add), new InlineProgress<SearchIssue>(issues.Add));
        Assert.Equal(summary.Progress, progress[^1]);
        return new(summary, results, issues);
    }

    private sealed record Outcome(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues);

    private sealed class FixtureExtractor(string[] segments, int warnings = 0, Func<string, Task>? afterSegments = null) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public SearchResourceLimits? SeenLimits { get; private set; }
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeenLimits = context.Limits;
            try
            {
                for (var index = 0; index < warnings; index++) context.Warn("fixture warning");
                await Task.Yield();
                for (var index = 0; index < segments.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new($"segment:{index}", segments[index], new(SourceKind.WordParagraph, Paragraph: index + 1));
                }
                if (afterSegments is not null) await afterSegments(path);
            }
            finally { Disposed = true; }
        }
    }
}
