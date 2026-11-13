using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class RegularExpressionSearchTests
{
    [Fact]
    public void DefaultsPreserveTheExistingConstructor()
    {
        var request = new SearchRequest("root", ["literal"]);
        Assert.Equal(SearchMatchMode.Literal, request.MatchMode);
        Assert.Null(request.RegexPattern);
        var limits = new SearchResourceLimits();
        Assert.Equal(4096, limits.MaxRegexPatternCharacters);
        Assert.Equal(100, limits.RegexMatchTimeoutMilliseconds);
        Assert.Equal(1000, limits.MaxRegexFileMilliseconds);
    }

    [Theory]
    [InlineData(@"\d+", "编号12 / 345", "12", "345")]
    [InlineData("西瓜+", "西瓜瓜和西瓜", "西瓜瓜", "西瓜")]
    [InlineData(@"(\d)(\d+)", "12 345", "12", "345")]
    [InlineData(@"(?<word>西瓜)\k<word>", "西瓜西瓜 西瓜西瓜", "西瓜西瓜", "西瓜西瓜")]
    [InlineData(@"(?<=编号)\d+(?=号)", "编号12号 编号345号", "12", "345")]
    [InlineData(@"(?i:abc)", "ABC abc", "ABC", "abc")]
    public async Task StandardDotNetSyntaxReturnsWholeMatches(string pattern, string text, string first, string second)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("hits.txt", text);
        var outcome = await RunAsync(Request(directory.Root, pattern, true));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        var matches = Assert.Single(outcome.Results).Matches;
        Assert.Equal(new[] { first, second }, matches.Select(match => match.Text));
        Assert.All(matches, match => Assert.Equal(pattern, match.Term));
        Assert.Equal(2, outcome.Summary.Progress.Occurrences);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task MatchCaseIsInvariantUnderTurkishCulture(bool matchCase, int expected)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("culture.txt", "FILE file FiLe");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var outcome = await RunAsync(Request(directory.Root, "file", matchCase));
            Assert.Equal(expected, Assert.Single(outcome.Results).TotalOccurrences);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("^|西瓜")]
    [InlineData("(?=前)|西瓜")]
    [InlineData("西瓜|a*")]
    public async Task EmptyMatchesAreSkippedAndNextMatchAdvances(string pattern)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("zero.txt", "前西瓜");
        var outcome = await RunAsync(Request(directory.Root, pattern), new() { MaxMatchesPerFile = 1 });
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        var match = Assert.Single(Assert.Single(outcome.Results).Matches);
        Assert.Equal("西瓜", match.Text);
        Assert.Equal(1, match.Offset);
    }

    [Theory]
    [InlineData("^")]
    [InlineData("$")]
    [InlineData("(?=西瓜)")]
    [InlineData("(?:)")]
    public async Task OnlyEmptyMatchesNeverCreateResults(string pattern)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("empty.txt", "西瓜");
        var outcome = await RunAsync(Request(directory.Root, pattern));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.Occurrences);
        Assert.Equal(1, outcome.Summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task CompleteMatchesAreNonOverlappingAndDoNotListCaptures()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("overlap.txt", "aaaaa");
        var outcome = await RunAsync(Request(directory.Root, "(a)(a)"));
        Assert.Equal(new[] { 0, 2 }, Assert.Single(outcome.Results).Matches.Select(match => match.Offset));
        Assert.All(outcome.Results[0].Matches, match => Assert.Equal("aa", match.Text));
    }

    [Fact]
    public async Task SourceOffsetsColumnsAndContextUseUtf16()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("source.txt", "😀前12后\n第345尾");
        var outcome = await RunAsync(Request(directory.Root, @"\d+") with { ContextCharacters = 2 });
        var matches = Assert.Single(outcome.Results).Matches;
        Assert.Equal(2, matches.Count);
        Assert.Equal(3, matches[0].Offset);
        Assert.Equal(4, matches[0].Location.Column);
        Assert.Equal(1, matches[0].Location.Line);
        Assert.Equal("😀前"[1..], matches[0].Before);
        Assert.Equal("12", matches[0].Text);
        Assert.Equal("后", matches[0].After);
        Assert.False(string.IsNullOrWhiteSpace(matches[0].SourceSegmentId));
        Assert.Equal(1, matches[1].Offset);
        Assert.Equal(2, matches[1].Location.Column);
        Assert.Equal(2, matches[1].Location.Line);
        Assert.Equal("第", matches[1].Before);
        Assert.Equal("345", matches[1].Text);
        Assert.Equal("尾", matches[1].After);
        Assert.NotEqual(matches[0].SourceSegmentId, matches[1].SourceSegmentId);
    }

    [Fact]
    public async Task RealSegmentsAreNotConcatenatedAndStructuredLocationsRemainUnchanged()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("segments.fake", "fixture");
        var source = new SourceLocator(SourceKind.ExcelCell, Sheet: "账簿", Address: "B2", ValueKind: "Text");
        var extractor = new FixtureExtractor(_ => [new("left", "西", source), new("right", "瓜", source), new("actual", "前西瓜后", source)]);
        var outcome = await RunAsync(Request(directory.Root, "西瓜"), extractor: extractor);
        var match = Assert.Single(Assert.Single(outcome.Results).Matches);
        Assert.Same(source, match.Location);
        Assert.Equal("actual", match.SourceSegmentId);
        Assert.Equal(1, match.Offset);
        Assert.Equal("前", match.Before);
        Assert.Equal("西瓜", match.Text);
        Assert.Equal("后", match.After);
        var spanning = await RunAsync(Request(directory.Root, "西.*瓜"), extractor: new FixtureExtractor(_ => [new("left", "西", source), new("right", "瓜", source)]));
        Assert.Empty(spanning.Results);
    }

    [Fact]
    public void ValidationReportsFrameworkOffsetWithoutEchoingPattern()
    {
        const string pattern = "PRIVATE_PATTERN(";
        var framework = Assert.Throws<RegexParseException>(() => new Regex(pattern));
        var error = RegularExpressionQuery.Validate(pattern);
        Assert.NotNull(error);
        Assert.Contains($"字符位置 {framework.Offset}", error);
        Assert.DoesNotContain(pattern, error);
        Assert.Null(RegularExpressionQuery.Validate(@"(?<=a)(b)\1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("(?<invalid")]
    public void InvalidPatternsRejectSynchronouslyBeforePathAccess(string? pattern)
    {
        var error = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(Request("\0unreadable", pattern)); });
        Assert.Contains("正则表达式", error.Message);
        Assert.NotNull(RegularExpressionQuery.Validate(pattern));
    }

    [Fact]
    public async Task WhitespacePatternIsPreservedAndMatchesRealSpaces()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("spaces.txt", "前  后");
        Assert.Null(RegularExpressionQuery.Validate("  "));
        var outcome = await RunAsync(Request(directory.Root, "  "));
        var match = Assert.Single(Assert.Single(outcome.Results).Matches);
        Assert.Equal("  ", match.Text);
        Assert.Equal("  ", match.Term);
        Assert.Equal(1, match.Offset);
    }

    [Fact]
    public void PatternLengthCountsUtf16BeforePathAccess()
    {
        var limits = new SearchResourceLimits { MaxRegexPatternCharacters = 2 };
        Assert.Null(RegularExpressionQuery.Validate("😀", limits: limits));
        Assert.Contains("MaxRegexPatternCharacters", RegularExpressionQuery.Validate("😀a", limits: limits));
        var error = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine(limits: limits).SearchAsync(Request("\0", "😀a")); });
        Assert.Contains("MaxRegexPatternCharacters", error.Message);
    }

    [Fact]
    public void ConflictingAndUndefinedModesRejectBeforePathAccess()
    {
        var valid = Request("\0", "a");
        SearchRequest[] invalid =
        [
            valid with { Terms = ["a"] }, valid with { Terms = null! },
            valid with { Expression = new TermExpression(0) }, valid with { TermConnectors = [] },
            valid with { MatchMode = (SearchMatchMode)999 },
            new("\0", ["a"]) { RegexPattern = "a" }, new("\0", ["a"]) { RegexPattern = "" }
        ];
        foreach (var request in invalid)
        {
            var error = Assert.Throws<ArgumentException>(() => { _ = new TextSearchEngine().SearchAsync(request); });
            Assert.Equal("request", error.ParamName);
            Assert.DoesNotContain("path", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EveryRegexBudgetRequiresPositiveValues(int value)
    {
        SearchResourceLimits[] invalid = [new() { MaxRegexPatternCharacters = value }, new() { RegexMatchTimeoutMilliseconds = value }, new() { MaxRegexFileMilliseconds = value }];
        foreach (var limits in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TextSearchEngine(limits: limits));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractionContext(_ => { }, limits: limits));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegularExpressionQuery.Validate("a", limits: limits));
        }
    }

    [Fact]
    public void TimeoutUpperBoundsAndSnapshotsAreValidated()
    {
        var accepted = new SearchResourceLimits { RegexMatchTimeoutMilliseconds = 1000, MaxRegexFileMilliseconds = 60_000 };
        var context = new ExtractionContext(_ => { }, limits: accepted);
        Assert.NotSame(accepted, context.Limits);
        Assert.Equal(accepted, context.Limits);
        Assert.Null(RegularExpressionQuery.Validate("a", limits: accepted));
        foreach (var invalid in new[] { accepted with { RegexMatchTimeoutMilliseconds = 1001 }, accepted with { MaxRegexFileMilliseconds = 60_001 } })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TextSearchEngine(limits: invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => RegularExpressionQuery.Validate("a", limits: invalid));
        }
    }

    [Fact]
    public async Task MatchTimeoutDiscardsWholeFileContinuesSurvivorAndKeepsIssuesPrivate()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("timeout.fake", "fixture");
        await directory.WriteAsync("survivor.fake", "fixture");
        const string pattern = @"西瓜|(a+)+$";
        var privateText = new string('a', 32_768) + "!PRIVATE_INPUT";
        var extractor = new FixtureExtractor(path => Path.GetFileName(path) == "timeout.fake" ? Segments("西瓜", privateText) : Segments("西瓜"));
        var outcome = await RunAsync(Request(directory.Root, pattern), new() { RegexMatchTimeoutMilliseconds = 25, MaxRegexFileMilliseconds = 60_000 }, extractor);
        Assert.Equal(SearchStatus.Partial, outcome.Summary.Status);
        Assert.Equal("survivor.fake", Path.GetFileName(Assert.Single(outcome.Results).FullPath));
        Assert.Equal(2, outcome.Summary.Progress.ScannedFiles);
        Assert.Equal(1, outcome.Summary.Progress.Occurrences);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal(SearchIssueCode.ResourceLimit, issue.Code);
        Assert.Contains("RegexMatchTimeoutMilliseconds=25", issue.Message);
        Assert.DoesNotContain(pattern, issue.Message);
        Assert.DoesNotContain("PRIVATE_INPUT", issue.Message);
        Assert.DoesNotContain(new string('a', 20), issue.Message);
    }

    [Fact]
    public async Task FileBudgetIncludesEmptyEnumerationAndDiscardsEarlierSegments()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("budget.fake", "fixture");
        // Large empty-match enumeration avoids depending on a narrowly tuned input duration.
        var extractor = new FixtureExtractor(_ => Segments("西瓜", new string('a', 1_000_000)));
        var outcome = await RunAsync(Request(directory.Root, "西瓜|(?=a)"), new() { RegexMatchTimeoutMilliseconds = 1000, MaxRegexFileMilliseconds = 1 }, extractor);
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Contains("MaxRegexFileMilliseconds=1", Assert.Single(outcome.Issues).Message);
        Assert.Equal(SearchIssueCode.ResourceLimit, outcome.Issues[0].Code);
    }

    [Fact]
    public async Task ExtractionDelayDoesNotConsumeMatchingBudget()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("slow-extraction.fake", "fixture");
        // This delay is deliberately outside matching. The two tiny matching operations
        // receive their own cumulative budget, rather than the extraction wall-clock time.
        var extractor = new FixtureExtractor(_ => Segments("西瓜", "西瓜"), beforeSecond: () => Thread.Sleep(150));
        var outcome = await RunAsync(Request(directory.Root, "西瓜"), new() { MaxRegexFileMilliseconds = 100 }, extractor);
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(2, Assert.Single(outcome.Results).TotalOccurrences);
    }

    [Theory]
    [InlineData(2, SearchStatus.Completed)]
    [InlineData(3, SearchStatus.Failed)]
    public async Task PerFileMatchesPreserveWholeFilePublication(int count, SearchStatus expected)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("matches.txt", string.Join(" ", Enumerable.Repeat("12", count)));
        var outcome = await RunAsync(Request(directory.Root, @"\d+"), new() { MaxMatchesPerFile = 2 });
        Assert.Equal(expected, outcome.Summary.Status);
        Assert.Equal(expected == SearchStatus.Completed ? count : 0, outcome.Summary.Progress.Occurrences);
        if (expected == SearchStatus.Failed)
        {
            Assert.Empty(outcome.Results);
            Assert.Contains("MaxMatchesPerFile=2", Assert.Single(outcome.Issues).Message);
        }
    }

    [Fact]
    public async Task GlobalMatchFileAndScannedBudgetsStillApply()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "12 34");
        await directory.WriteAsync("two.txt", "56 78");
        var total = await RunAsync(Request(directory.Root, @"\d+"), new() { MaxTotalMatches = 3 });
        Assert.Equal(SearchStatus.Limited, total.Summary.Status);
        Assert.Single(total.Results);
        Assert.Equal(2, total.Summary.Progress.Occurrences);
        Assert.Contains("MaxTotalMatches=3", total.Summary.Diagnostics.LimitReason);
        var files = await RunAsync(Request(directory.Root, @"\d+"), new() { MaxMatchedFiles = 1 });
        Assert.Equal(SearchStatus.Limited, files.Summary.Status);
        Assert.Single(files.Results);
        var scanned = await RunAsync(Request(directory.Root, @"\d+"), new() { MaxScannedFiles = 1 });
        Assert.Equal(SearchStatus.Limited, scanned.Summary.Status);
        Assert.Equal(1, scanned.Summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task SegmentBudgetStillRejectsWholeFile()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("long.fake", "fixture");
        var outcome = await RunAsync(Request(directory.Root, "a"), new() { MaxSegmentCharacters = 4 }, new FixtureExtractor(_ => Segments("a", "aaaaa")));
        Assert.Equal(SearchStatus.Failed, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Contains("MaxSegmentCharacters=4", Assert.Single(outcome.Issues).Message);
    }

    [Fact]
    public async Task PreCancellationDoesNotExtract()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.fake", "fixture");
        var extracted = false;
        var extractor = new FixtureExtractor(_ => { extracted = true; return Segments("a"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await RunAsync(Request(directory.Root, "a"), extractor: extractor, token: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.False(extracted);
    }

    [Fact]
    public async Task CancellationBetweenSegmentsDiscardsPendingMatchesAndDisposesExtractor()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("cancel.fake", "fixture");
        using var cancellation = new CancellationTokenSource();
        var extractor = new FixtureExtractor(_ => Segments("前西瓜", "前西瓜"), beforeSecond: () => cancellation.Cancel());
        var outcome = await RunAsync(Request(directory.Root, "^|西瓜"), extractor: extractor, token: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, outcome.Summary.Status);
        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.Summary.Progress.ScannedFiles);
        Assert.True(extractor.Disposed);
    }

    [Fact]
    public async Task CancellationAtFirstPublishedMatchPreservesBackpressure()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.txt", "12");
        await directory.WriteAsync("two.txt", "34");
        using var cancellation = new CancellationTokenSource();
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine().SearchAsync(Request(directory.Root, @"\d+"),
            progress: new InlineProgress<SearchProgress>(value => { if (value.MatchedFiles == 1) cancellation.Cancel(); }),
            results: new InlineProgress<FileResult>(results.Add), cancellationToken: cancellation.Token);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Single(results);
        Assert.Equal(1, summary.Progress.ScannedFiles);
    }

    [Fact]
    public async Task MetadataChecksRemainAndSearchNeverWritesSource()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("source.fake", "fixture");
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var clean = await RunAsync(Request(directory.Root, "西瓜"), extractor: new FixtureExtractor(_ => Segments("西瓜")));
        Assert.Equal(SearchStatus.Completed, clean.Summary.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(modified, Assert.Single(clean.Results).LastWriteTimeUtc);
        var changed = await RunAsync(Request(directory.Root, "西瓜"), extractor: new FixtureExtractor(_ => Segments("西瓜"), afterSegments: source => File.SetLastWriteTimeUtc(source, modified.AddMinutes(1))));
        Assert.Equal(SearchStatus.Failed, changed.Summary.Status);
        Assert.Empty(changed.Results);
        Assert.Equal(SearchIssueCode.FileChanged, Assert.Single(changed.Issues).Code);
    }

    [Fact]
    public async Task ExistingLiteralModeNeverParsesRegexSyntax()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("literal.txt", @"[ ( \d+ 123");
        var outcome = await RunAsync(new(directory.Root, ["[", "(", @"\d+"]));
        Assert.Equal(SearchStatus.Completed, outcome.Summary.Status);
        Assert.Equal(new[] { "[", "(", @"\d+" }, Assert.Single(outcome.Results).Matches.Select(match => match.Text));
    }

    private static SearchRequest Request(string root, string? pattern, bool matchCase = false) =>
        new(root, [], MatchCase: matchCase) { MatchMode = SearchMatchMode.RegularExpression, RegexPattern = pattern };
    private static TextSegment[] Segments(params string[] texts) => texts.Select((text, index) =>
        new TextSegment($"segment:{index}", text, new(SourceKind.WordParagraph, Paragraph: index + 1))).ToArray();
    private static async Task<Outcome> RunAsync(SearchRequest request, SearchResourceLimits? limits = null, ITextExtractor? extractor = null, CancellationToken token = default)
    {
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var summary = await new TextSearchEngine(extractor is null ? null : [extractor], limits).SearchAsync(request,
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add), cancellationToken: token);
        return new(summary, results, issues);
    }
    private sealed record Outcome(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues);
    private sealed class FixtureExtractor(Func<string, TextSegment[]> segments, Action? beforeSecond = null, Action<string>? afterSegments = null) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            try
            {
                var items = segments(path);
                for (var index = 0; index < items.Length; index++)
                {
                    if (index == 1) beforeSecond?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return items[index];
                }
                afterSegments?.Invoke(path);
            }
            finally { Disposed = true; }
        }
    }
}
