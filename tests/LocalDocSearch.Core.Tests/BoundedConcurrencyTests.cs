using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class BoundedConcurrencyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void RejectsUnsupportedWidthBeforeRequestValidation(int width)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new TextSearchEngine().SearchAsync(null!, maxConcurrency: width);
        });
        Assert.Equal("maxConcurrency", error.ParamName);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RealParallelismAndFifoWindowIncludeCompletedUnpublishedFiles(int width)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < width * 3; index++) await directory.WriteAsync($"{index}.fake", "a");
        var order = Directory.EnumerateFiles(directory.Root).ToArray();
        var full = Signal();
        var followersDone = Signal();
        var release = Signal();
        var active = 0;
        var peak = 0;
        var completedFollowers = 0;
        var extractor = new Probe(async (path, _, token) =>
        {
            var now = Interlocked.Increment(ref active);
            UpdateMaximum(ref peak, now);
            if (now == width) full.TrySetResult();
            // Every worker enters before any exits; the head remains blocked afterwards.
            await full.Task.WaitAsync(token);
            if (path == order[0]) await release.Task.WaitAsync(token);
            else if (Interlocked.Increment(ref completedFollowers) == width - 1) followersDone.TrySetResult();
            Interlocked.Decrement(ref active);
        });
        using var stop = new CancellationTokenSource();
        var published = new ConcurrentQueue<string>();
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(file => published.Enqueue(file.FullPath)),
            cancellationToken: stop.Token, maxConcurrency: width);
        try
        {
            await followersDone.Task.WaitAsync(Deadline);
            Assert.Equal(width, extractor.Started);
            Assert.Equal(width, peak);
            Assert.Empty(published);
            // Completed followers do not free a window slot ahead of the head.
            release.TrySetResult();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Equal(order, published);
            Assert.Equal(order.Length, extractor.Disposed);
            Assert.InRange(extractor.Peak, 2, width);
        }
        finally { release.TrySetResult(); stop.Cancel(); await search.WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ParallelAndSequentialPreserveDfsFormulaRegexWarningsAndFailures(int width)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("root.fake", "a1 a2 b");
        await directory.WriteAsync("bad.fake", "a1 a2 b");
        await directory.WriteAsync(Path.Combine("child-a", "a.fake"), "a1 a2 b");
        await directory.WriteAsync(Path.Combine("child-b", "b.fake"), "a1 a2 b");
        foreach (var regex in new[] { false, true })
        {
            var request = regex ? new SearchRequest(directory.Root, [])
            { MatchMode = SearchMatchMode.RegularExpression, RegexPattern = "a[0-9]" } :
                new SearchRequest(directory.Root, ["a", "b", "absent"], SearchOperator.Or)
                { TermConnectors = [SearchOperator.And, SearchOperator.Or] };
            foreach (var (maxIssues, maxWarnings) in new[] { (5, 2), (2, 20) })
            {
                var baseline = await CaptureAsync(request, 1, maxIssues, maxWarnings);
                var parallel = await CaptureAsync(request, width, maxIssues, maxWarnings);
                Assert.Equal(baseline.Summary.Status, parallel.Summary.Status);
                Assert.Equal(baseline.Summary.Progress, parallel.Summary.Progress);
                Assert.Equal(baseline.Summary.Diagnostics.WarningCount, parallel.Summary.Diagnostics.WarningCount);
                Assert.Equal(baseline.Summary.Diagnostics.ErrorCount, parallel.Summary.Diagnostics.ErrorCount);
                Assert.Equal(baseline.Summary.Diagnostics.SuppressedIssueCount, parallel.Summary.Diagnostics.SuppressedIssueCount);
                Assert.Equal(baseline.Issues, parallel.Issues);
                Assert.Equal(baseline.Results.Select(file => file.FullPath), parallel.Results.Select(file => file.FullPath));
                for (var index = 0; index < baseline.Results.Count; index++)
                {
                    Assert.Equal(baseline.Results[index].Matches, parallel.Results[index].Matches);
                    Assert.Equal(baseline.Results[index].ExtractionWarnings, parallel.Results[index].ExtractionWarnings);
                }
            }
        }

        async Task<(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues)> CaptureAsync(
            SearchRequest request, int concurrency, int maxIssues, int maxWarnings)
        {
            var results = new List<FileResult>();
            var issues = new List<SearchIssue>();
            var extractor = new Probe((path, context, _) =>
            {
                for (var index = 0; index < 9; index++) context.Warn($"warning-{index}");
                if (Path.GetFileName(path) == "bad.fake") throw new IOException("fixture failure");
                return Task.CompletedTask;
            }, "a1 a2 b");
            var summary = await new TextSearchEngine([extractor], new() { MaxIssueDetails = maxIssues, MaxWarningsPerFile = maxWarnings })
                .SearchAsync(request, results: new InlineProgress<FileResult>(results.Add),
                    issues: new InlineProgress<SearchIssue>(issues.Add), maxConcurrency: concurrency).WaitAsync(Deadline);
            return (summary, results, issues);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task AllLogicalBudgetsRetainSequentialSummaryAndDiagnosticOrder(int width)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < 3; index++) await directory.WriteAsync($"{index}.fake", "aa");
        await directory.WriteAsync(Path.Combine("child-a", "grandchild", "a.fake"), "aa");
        await directory.WriteAsync(Path.Combine("child-b", "b.fake"), "aa");
        SearchResourceLimits[] budgets =
        [
            new() { MaxScannedFiles = 1 }, new() { MaxMatchedFiles = 1 },
            new() { MaxTotalMatches = 3 }, new() { MaxTotalResultCharacters = 5 },
            new() { MaxMatchesPerFile = 1 }, new() { MaxResultCharactersPerFile = 1 },
            new() { MaxSegmentCharacters = 1 }, new() { MaxDirectoryDepth = 1 },
            new() { MaxPendingDirectories = 1 }
        ];
        foreach (var limits in budgets)
        {
            var baseline = await CaptureAsync(1, limits);
            var parallel = await CaptureAsync(width, limits);
            Assert.Equal(baseline.Summary.Status, parallel.Summary.Status);
            Assert.Equal(baseline.Summary.Progress, parallel.Summary.Progress);
            Assert.Equal(baseline.Summary.Diagnostics.LimitReason, parallel.Summary.Diagnostics.LimitReason);
            Assert.Equal(baseline.Summary.Diagnostics.ErrorCount, parallel.Summary.Diagnostics.ErrorCount);
            Assert.Equal(baseline.Issues, parallel.Issues);
            Assert.Equal(baseline.Results.Select(file => file.FullPath), parallel.Results.Select(file => file.FullPath));
        }
        async Task<(SearchSummary Summary, List<FileResult> Results, List<SearchIssue> Issues)> CaptureAsync(
            int concurrency, SearchResourceLimits limits)
        {
            var files = new List<FileResult>();
            var issues = new List<SearchIssue>();
            var summary = await new TextSearchEngine([new Probe((_, _, _) => Task.CompletedTask, "aa")], limits)
                .SearchAsync(new(directory.Root, ["a"]), results: new InlineProgress<FileResult>(files.Add),
                    issues: new InlineProgress<SearchIssue>(issues.Add), maxConcurrency: concurrency).WaitAsync(Deadline);
            return (summary, files, issues);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task SlowWorkerPreventsFalsePauseAndResumeOrCancelJoinsEverything(int width, bool cancel)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < width; index++) await directory.WriteAsync($"{index}.fake", "a");
        var order = Directory.EnumerateFiles(directory.Root).ToArray();
        var allEntered = Signal();
        var releaseFollowers = Signal();
        var releaseSlow = Signal();
        var followersReturned = Signal();
        var entered = 0;
        var returned = 0;
        var control = new SearchExecutionControl();
        using var stop = new CancellationTokenSource();
        var extractor = new Probe(async (path, _, token) =>
        {
            if (Interlocked.Increment(ref entered) == width) allEntered.TrySetResult();
            if (path == order[0]) await releaseSlow.Task.WaitAsync(token);
            else
            {
                await releaseFollowers.Task.WaitAsync(token);
                if (Interlocked.Increment(ref returned) == width - 1) followersReturned.TrySetResult();
            }
        });
        var results = new ConcurrentQueue<FileResult>();
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue), cancellationToken: stop.Token,
            executionControl: control, maxConcurrency: width);
        try
        {
            await allEntered.Task.WaitAsync(Deadline);
            control.Pause();
            releaseFollowers.TrySetResult();
            await followersReturned.Task.WaitAsync(Deadline);
            // A public waiter must not mask the still-active slow participant either.
            var independent = control.WaitIfPausedAsync(stop.Token).AsTask();
            Assert.False(control.IsPaused);
            releaseSlow.TrySetResult();
            await WaitForPauseAsync(control, search);
            Assert.Empty(results);
            Assert.Equal(width, extractor.Started);
            if (cancel) stop.Cancel(); else control.Resume();
            var summary = await search.WaitAsync(Deadline);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => independent);
            else await independent.WaitAsync(Deadline);
            Assert.Equal(cancel ? SearchStatus.Cancelled : SearchStatus.Completed, summary.Status);
            Assert.Equal(cancel ? 0 : width, results.Count);
            Assert.Equal(extractor.Started, extractor.Disposed);
            Assert.False(control.IsPaused);
        }
        finally
        {
            releaseSlow.TrySetResult(); releaseFollowers.TrySetResult();
            stop.Cancel(); control.Resume(); await search.WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task FirstFileProgressCancellationStartsNoExtractor(int width)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "a");
        var extractor = new Probe((_, _, _) => Task.CompletedTask);
        using var stop = new CancellationTokenSource();
        var summary = await new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            progress: new InlineProgress<SearchProgress>(value => { if (value.CurrentPath is not null) stop.Cancel(); }),
            cancellationToken: stop.Token, maxConcurrency: width).WaitAsync(Deadline);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Equal(0, extractor.Started);
        Assert.Equal(0, summary.Progress.ScannedFiles);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task FirstResultCancellationAndGlobalLimitJoinSpeculativeWorkers(int width, bool globalLimit)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < width + 2; index++) await directory.WriteAsync($"{index}.fake", "a");
        using var stop = new CancellationTokenSource();
        var allEntered = Signal();
        var entered = 0;
        var extractor = new Probe(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref entered) == width) allEntered.TrySetResult();
            await allEntered.Task.WaitAsync(token);
        });
        var results = new List<FileResult>();
        var summary = await new TextSearchEngine([extractor], new() { MaxTotalMatches = 1 })
            .SearchAsync(new(directory.Root, ["a"]), results: new InlineProgress<FileResult>(file =>
            {
                results.Add(file);
                if (!globalLimit) stop.Cancel();
            }), cancellationToken: stop.Token, maxConcurrency: width).WaitAsync(Deadline);
        Assert.Equal(globalLimit ? SearchStatus.Limited : SearchStatus.Cancelled, summary.Status);
        Assert.Single(results);
        Assert.Equal(1, summary.Progress.Occurrences);
        Assert.Equal(extractor.Started, extractor.Disposed);
        Assert.Equal(0, extractor.Active);
        Assert.InRange(extractor.Peak, 2, width);
        if (globalLimit) Assert.Equal("MaxTotalMatches=1", summary.Diagnostics.LimitReason);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task UnexpectedLaterFaultCancelsBlockedHeadAndJoinsAllWorkers(int width)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < width; index++) await directory.WriteAsync($"{index}.fake", "a");
        var first = Directory.EnumerateFiles(directory.Root).First();
        var entered = Signal();
        var extractor = new Probe(async (path, _, token) =>
        {
            if (path == first)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            else
            {
                await entered.Task.WaitAsync(token);
                throw new InvalidOperationException("fatal fixture");
            }
        });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]), maxConcurrency: width).WaitAsync(Deadline));
        Assert.Equal("fatal fixture", exception.Message);
        Assert.Equal(extractor.Started, extractor.Disposed);
        Assert.Equal(0, extractor.Active);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MetadataChangesDiscardWholeFilesAndDisposeEveryEnumeration(int width)
    {
        using var directory = new SearchTestDirectory();
        for (var index = 0; index < width; index++) await directory.WriteAsync($"{index}.fake", "a");
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var extractor = new Probe(async (path, _, token) => await File.AppendAllTextAsync(path, "changed", token));
        var summary = await new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add),
            maxConcurrency: width).WaitAsync(Deadline);
        Assert.Equal(SearchStatus.Failed, summary.Status);
        Assert.Empty(results);
        Assert.Equal(width, summary.Progress.ScannedFiles);
        Assert.Equal(width, summary.Progress.SkippedFiles);
        Assert.Equal(width, issues.Count);
        Assert.All(issues, issue => Assert.Equal(SearchIssueCode.FileChanged, issue.Code));
        Assert.Equal(width, extractor.Disposed);
        Assert.Equal(0, extractor.Active);
    }

    [Fact]
    public async Task ScopedParticipantStartIdleExitAndPauseGenerationsAreDeterministic()
    {
        // White-box lifecycle coverage without publishing an experiment-only public API.
        var control = new SearchExecutionControl();
        using var coordinator = Register(control);
        using var worker = Register(control); // registered before its scheduled task starts
        var coordinatorControl = ParticipantControl(coordinator);
        var workerControl = ParticipantControl(worker);
        control.Pause();
        var coordinatorWait = coordinatorControl.WaitIfPausedAsync().AsTask();
        Assert.False(control.IsPaused); // worker has not reached its first checkpoint
        var workerWait = workerControl.WaitIfPausedAsync().AsTask();
        Assert.True(control.IsPaused);
        control.Resume();
        await Task.WhenAll(coordinatorWait, workerWait).WaitAsync(Deadline);
        control.Pause();
        var currentWait = workerControl.WaitIfPausedAsync().AsTask();
        Assert.False(currentWait.IsCompleted);
        Assert.False(control.IsPaused); // the coordinator's prior-generation ack is gone
        var currentCoordinatorWait = coordinatorControl.WaitIfPausedAsync().AsTask();
        Assert.True(control.IsPaused);
        control.Resume();
        await Task.WhenAll(currentCoordinatorWait, currentWait).WaitAsync(Deadline);
        SetIdle(coordinator, true);
        control.Pause();
        using var cancellation = new CancellationTokenSource();
        var pausedWorker = workerControl.WaitIfPausedAsync(cancellation.Token).AsTask();
        Assert.True(control.IsPaused); // idle coordinator cannot deadlock the pause
        SetIdle(coordinator, false);
        Assert.False(control.IsPaused); // waking coordinator must checkpoint before publishing
        SetIdle(coordinator, true);
        Assert.True(control.IsPaused);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pausedWorker);
        worker.Dispose();
        Assert.False(control.IsPaused); // no active participants is not acknowledgement
        control.Resume();
    }

    private static IDisposable Register(SearchExecutionControl control) => (IDisposable)typeof(SearchExecutionControl)
        .GetMethod("RegisterParticipant", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, null)!;
    private static SearchExecutionControl ParticipantControl(IDisposable participant) => (SearchExecutionControl)participant.GetType()
        .GetProperty("Control", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(participant)!;
    private static void SetIdle(IDisposable participant, bool idle) => participant.GetType()
        .GetMethod("SetIdle", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(participant, [idle]);

    private static async Task WaitForPauseAsync(SearchExecutionControl control, Task search) =>
        await UntilAsync(() => { Assert.False(search.IsCompleted); return control.IsPaused; });
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static void UpdateMaximum(ref int peak, int value)
    {
        int prior;
        do { prior = Volatile.Read(ref peak); if (value <= prior) return; }
        while (Interlocked.CompareExchange(ref peak, value, prior) != prior);
    }

    // Deliberately shared instance: per-enumeration locals are independent, counters synchronized.
    // A stateful third-party extractor needs the same property before opting in to width 2/4.
    private sealed class Probe(Func<string, ExtractionContext, CancellationToken, Task> beforeYield, string text = "a") : ITextExtractor
    {
        private int _started, _disposed, _active, _peak;
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public int Started => Volatile.Read(ref _started);
        public int Disposed => Volatile.Read(ref _disposed);
        public int Active => Volatile.Read(ref _active);
        public int Peak => Volatile.Read(ref _peak);
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            UpdateMaximum(ref _peak, Interlocked.Increment(ref _active));
            try
            {
                await beforeYield(path, context, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TextSegment("segment", text, new SourceLocation(1, 1));
            }
            finally { Interlocked.Decrement(ref _active); Interlocked.Increment(ref _disposed); }
        }
    }
}
