using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class SearchExecutionControlTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void RunningCheckpointCompletesSynchronouslyWithoutAcknowledgingPause()
    {
        var control = new SearchExecutionControl();
        Assert.False(control.IsPauseRequested);
        Assert.False(control.IsPaused);
        Assert.True(control.WaitIfPausedAsync().IsCompletedSuccessfully);
        control.Resume();
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task PauseRequestRequiresAWaitingCallerAndRepeatedPauseKeepsTheSameGate()
    {
        var control = new SearchExecutionControl();
        control.Pause();
        Assert.True(control.IsPauseRequested);
        Assert.False(control.IsPaused);
        var first = control.WaitIfPausedAsync().AsTask();
        Assert.True(control.IsPaused);
        control.Pause();
        var second = control.WaitIfPausedAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        control.Resume();
        control.Resume();
        await Task.WhenAll(first, second).WaitAsync(Deadline);
        Assert.False(control.IsPauseRequested);
        Assert.False(control.IsPaused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancellationWinsOverBothRunningAndPausedStates(bool paused)
    {
        var control = new SearchExecutionControl();
        if (paused) control.Pause();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await control.WaitIfPausedAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(control.IsPaused);
        Assert.Equal(paused, control.IsPauseRequested);
    }

    [Fact]
    public async Task ResumeReleasesEveryWaiter()
    {
        var control = new SearchExecutionControl();
        control.Pause();
        var waiters = Enumerable.Range(0, 32).Select(_ => control.WaitIfPausedAsync().AsTask()).ToArray();
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
        control.Resume();
        await Task.WhenAll(waiters).WaitAsync(Deadline);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotReleaseOrCancelOtherWaiters()
    {
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        control.Pause();
        var cancelled = control.WaitIfPausedAsync(cancellation.Token).AsTask();
        var surviving = control.WaitIfPausedAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Deadline));
        Assert.True(control.IsPauseRequested);
        Assert.True(control.IsPaused);
        Assert.False(surviving.IsCompleted);
        control.Resume();
        await surviving.WaitAsync(Deadline);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task LastCancelledWaiterClearsAcknowledgementButKeepsTheRequestReusable()
    {
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        control.Pause();
        var waiters = Enumerable.Range(0, 8)
            .Select(_ => control.WaitIfPausedAsync(cancellation.Token).AsTask()).ToArray();
        cancellation.Cancel();
        foreach (var waiter in waiters)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Deadline));
        Assert.False(control.IsPaused);
        Assert.True(control.IsPauseRequested);
        var next = control.WaitIfPausedAsync().AsTask();
        Assert.True(control.IsPaused);
        Assert.False(next.IsCompleted);
        control.Resume();
        await next.WaitAsync(Deadline);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task RapidPauseResumePauseCyclesKeepNewGenerationWaitersBlocked()
    {
        var control = new SearchExecutionControl();
        var priorWaiters = new List<Task>();
        for (var generation = 0; generation < 128; generation++)
        {
            control.Pause();
            priorWaiters.Add(control.WaitIfPausedAsync().AsTask());
            control.Resume();
            control.Pause();
            var current = control.WaitIfPausedAsync().AsTask();
            priorWaiters.Add(current);
            control.Pause();
            Assert.True(control.IsPauseRequested);
            Assert.True(control.IsPaused);
            Assert.False(current.IsCompleted);
        }
        control.Resume();
        await Task.WhenAll(priorWaiters).WaitAsync(Deadline);
        Assert.False(control.IsPauseRequested);
        Assert.False(control.IsPaused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchHonorsPrePauseBeforeScanningAndEitherResumesOrStops(bool stop)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        var extractor = new ProbeExtractor(["a"]);
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        control.Pause();
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await WaitForPauseAsync(control, search);
            Assert.Equal(0, extractor.Started);
            Assert.Equal(0, extractor.MoveNextCalls);
            Assert.Empty(results);
            Assert.False(search.IsCompleted);
            if (stop) cancellation.Cancel(); else control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(stop ? SearchStatus.Cancelled : SearchStatus.Completed, summary.Status);
            Assert.Equal(stop ? 0 : 1, summary.Progress.ScannedFiles);
            Assert.Equal(stop ? 0 : 1, results.Count);
            Assert.Equal(stop ? 0 : 1, extractor.Disposed);
            Assert.False(control.IsPaused);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Fact]
    public async Task PreCancelledPrePausedSearchStillReturnsCancelledSummary()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        var extractor = new ProbeExtractor(["a"]);
        var control = new SearchExecutionControl();
        control.Pause();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var summary = await new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            cancellationToken: cancellation.Token, executionControl: control).WaitAsync(Deadline);
        Assert.Equal(SearchStatus.Cancelled, summary.Status);
        Assert.Equal(0, summary.Progress.ScannedFiles);
        Assert.Equal(0, extractor.Started);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task PauseAtFirstFileProgressStopsBeforeOpeningTheExtractor()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("one.fake", "fixture");
        var control = new SearchExecutionControl();
        var extractor = new ProbeExtractor(["a", "b"]);
        using var cancellation = new CancellationTokenSource();
        var progressReached = Signal();
        var results = new ConcurrentQueue<FileResult>();
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a", "b"]),
            progress: new InlineProgress<SearchProgress>(value =>
            {
                if (value.CurrentPath != path) return;
                control.Pause();
                progressReached.TrySetResult();
            }), results: new InlineProgress<FileResult>(results.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await progressReached.Task.WaitAsync(Deadline);
            await WaitForPauseAsync(control, search);
            Assert.Equal(0, extractor.Started);
            cancellation.Cancel();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Cancelled, summary.Status);
            Assert.Equal(0, summary.Progress.ScannedFiles);
            Assert.Empty(results);
            Assert.False(control.IsPaused);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausingOnFirstResultPreventsFurtherScanningAndPreservesDfsOrder(bool stop)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("root.fake", "fixture");
        await directory.WriteAsync(Path.Combine("child-a", "a.fake"), "fixture");
        await directory.WriteAsync(Path.Combine("child-b", "b.fake"), "fixture");
        var baseline = new List<FileResult>();
        await new TextSearchEngine([new ProbeExtractor(["a", "b"])]).SearchAsync(new(directory.Root, ["a", "b"]),
            results: new InlineProgress<FileResult>(baseline.Add)).WaitAsync(Deadline);
        var control = new SearchExecutionControl();
        var extractor = new ProbeExtractor(["a", "b"]);
        using var cancellation = new CancellationTokenSource();
        var firstResult = Signal();
        var results = new ConcurrentQueue<FileResult>();
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a", "b"]),
            results: new InlineProgress<FileResult>(result =>
            {
                results.Enqueue(result);
                if (results.Count != 1) return;
                control.Pause();
                firstResult.TrySetResult();
            }), cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await firstResult.Task.WaitAsync(Deadline);
            await WaitForPauseAsync(control, search);
            Assert.Single(results);
            Assert.Equal(1, extractor.Started);
            Assert.Equal(1, extractor.Disposed);
            Assert.Equal(3, extractor.MoveNextCalls);
            Assert.False(search.IsCompleted);
            if (stop) cancellation.Cancel(); else control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(stop ? SearchStatus.Cancelled : SearchStatus.Completed, summary.Status);
            Assert.Equal(stop ? 1 : 3, summary.Progress.ScannedFiles);
            Assert.Equal(stop ? 2 : 6, summary.Progress.Occurrences);
            Assert.Equal(baseline.Take(stop ? 1 : 3).Select(item => item.FullPath), results.Select(item => item.FullPath));
            Assert.False(control.IsPaused);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseInsideMoveNextIsAcknowledgedAfterItReturnsAndDisposesOnStop(bool stop)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        var entered = Signal();
        var release = Signal();
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var extractor = new ProbeExtractor(["a", "b"], async (_, index, token) =>
        {
            if (index != 0) return;
            control.Pause();
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a", "b"]),
            results: new InlineProgress<FileResult>(results.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.True(control.IsPauseRequested);
            Assert.False(control.IsPaused);
            Assert.Empty(results);
            release.TrySetResult();
            await WaitForPauseAsync(control, search);
            Assert.Equal(1, extractor.MoveNextCalls);
            Assert.Equal(0, extractor.Disposed);
            Assert.Empty(results);
            if (stop) cancellation.Cancel(); else control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(stop ? SearchStatus.Cancelled : SearchStatus.Completed, summary.Status);
            Assert.Equal(stop ? 0 : 1, summary.Progress.ScannedFiles);
            Assert.Equal(stop ? 0 : 2, summary.Progress.Occurrences);
            Assert.Equal(stop ? 1 : 3, extractor.MoveNextCalls);
            Assert.Equal(1, extractor.Disposed);
            Assert.False(control.IsPaused);
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(control, cancellation, search);
        }
    }

    [Fact]
    public async Task PauseOnTerminalMoveNextStillRequiresCompleteEnumerationBeforePublishing()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        var control = new SearchExecutionControl();
        var terminalReached = Signal();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var extractor = new ProbeExtractor(["a"], (_, index, _) =>
        {
            if (index == 1)
            {
                control.Pause();
                terminalReached.TrySetResult();
            }
            return Task.CompletedTask;
        });
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await terminalReached.Task.WaitAsync(Deadline);
            await WaitForPauseAsync(control, search);
            Assert.Empty(results);
            Assert.Equal(2, extractor.MoveNextCalls);
            Assert.Equal(1, extractor.Disposed);
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Single(results);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseResumeKeepsLateFailureAndFileBudgetAtomic(bool resourceLimit)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("bad.fake", "fixture");
        await directory.WriteAsync("good.fake", "fixture");
        var control = new SearchExecutionControl();
        var pausedFile = Signal();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var issues = new ConcurrentQueue<SearchIssue>();
        var extractor = new ProbeExtractor(["a", "a"], (path, index, _) =>
        {
            if (!Path.GetFileName(path).Equals("bad.fake", StringComparison.Ordinal)) return Task.CompletedTask;
            if (index == 1)
            {
                control.Pause();
                pausedFile.TrySetResult();
            }
            if (index == 2)
            {
                if (resourceLimit)
                    throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxMatchesPerFile), 2);
                throw new IOException("late fixture read failure");
            }
            return Task.CompletedTask;
        });
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue), issues: new InlineProgress<SearchIssue>(issues.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await pausedFile.Task.WaitAsync(Deadline);
            await WaitForPauseAsync(control, search);
            Assert.DoesNotContain(results, result => Path.GetFileName(result.FullPath) == "bad.fake");
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Partial, summary.Status);
            Assert.Equal("good.fake", Path.GetFileName(Assert.Single(results).FullPath));
            Assert.Equal(2, summary.Progress.Occurrences);
            Assert.Equal(2, summary.Progress.ScannedFiles);
            Assert.Equal(1, summary.Progress.SkippedFiles);
            Assert.Equal(resourceLimit ? SearchIssueCode.ResourceLimit : SearchIssueCode.Unknown, Assert.Single(issues).Code);
            Assert.Equal(2, extractor.Disposed);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Fact]
    public async Task PauseResumeKeepsEngineMatchBudgetAndWholeFileDiscard()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("over-budget.fake", "fixture");
        var control = new SearchExecutionControl();
        var reached = Signal();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var issues = new ConcurrentQueue<SearchIssue>();
        var extractor = new ProbeExtractor(["a", "aa"], (_, index, _) =>
        {
            if (index == 1) { control.Pause(); reached.TrySetResult(); }
            return Task.CompletedTask;
        });
        var search = new TextSearchEngine([extractor], new() { MaxMatchesPerFile = 2 }).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue), issues: new InlineProgress<SearchIssue>(issues.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await reached.Task.WaitAsync(Deadline);
            await WaitForPauseAsync(control, search);
            Assert.Empty(results);
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Failed, summary.Status);
            Assert.Empty(results);
            Assert.Equal(0, summary.Progress.Occurrences);
            Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(issues).Code);
            Assert.Equal(1, extractor.Disposed);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Fact]
    public async Task PauseResumeKeepsGlobalBudgetAndPreviouslyPublishedWholeFile()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("one.fake", "fixture");
        await directory.WriteAsync("two.fake", "fixture");
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var issues = new ConcurrentQueue<SearchIssue>();
        var extractor = new ProbeExtractor(["aa"]);
        var search = new TextSearchEngine([extractor], new() { MaxTotalMatches = 3 }).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(result => { results.Enqueue(result); control.Pause(); }),
            issues: new InlineProgress<SearchIssue>(issues.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await WaitForPauseAsync(control, search);
            Assert.Equal(2, Assert.Single(results).TotalOccurrences);
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Limited, summary.Status);
            Assert.Equal(2, summary.Progress.ScannedFiles);
            Assert.Equal(2, summary.Progress.Occurrences);
            Assert.Single(results);
            Assert.Equal(SearchIssueCode.ResourceLimit, Assert.Single(issues).Code);
            Assert.Equal("MaxTotalMatches=3", summary.Diagnostics.LimitReason);
            Assert.Equal(2, extractor.Disposed);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Fact]
    public async Task SourceChangedWhilePausedDiscardsTheCompleteFile()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("changed.fake", "fixture");
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var issues = new ConcurrentQueue<SearchIssue>();
        var extractor = new ProbeExtractor(["a"], (_, index, _) =>
        {
            if (index == 1) control.Pause();
            return Task.CompletedTask;
        });
        var search = new TextSearchEngine([extractor]).SearchAsync(new(directory.Root, ["a"]),
            results: new InlineProgress<FileResult>(results.Enqueue), issues: new InlineProgress<SearchIssue>(issues.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await WaitForPauseAsync(control, search);
            await File.AppendAllTextAsync(path, "changed");
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Failed, summary.Status);
            Assert.Empty(results);
            Assert.Equal(SearchIssueCode.FileChanged, Assert.Single(issues).Code);
            Assert.Equal(1, extractor.Disposed);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    [Fact]
    public async Task RegexSearchResumesAtSegmentBoundaryWithAllOccurrences()
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("regex.fake", "fixture");
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var results = new ConcurrentQueue<FileResult>();
        var extractor = new ProbeExtractor(["a1 a2", "a3"], (_, index, _) =>
        {
            if (index == 1) control.Pause();
            return Task.CompletedTask;
        });
        var request = new SearchRequest(directory.Root, [])
        {
            MatchMode = SearchMatchMode.RegularExpression,
            RegexPattern = "a[0-9]"
        };
        var search = new TextSearchEngine([extractor]).SearchAsync(request,
            results: new InlineProgress<FileResult>(results.Enqueue),
            cancellationToken: cancellation.Token, executionControl: control);
        try
        {
            await WaitForPauseAsync(control, search);
            Assert.Empty(results);
            Assert.Equal(2, extractor.MoveNextCalls);
            control.Resume();
            var summary = await search.WaitAsync(Deadline);
            Assert.Equal(SearchStatus.Completed, summary.Status);
            Assert.Equal(new[] { "a1", "a2", "a3" }, Assert.Single(results).Matches.Select(item => item.Text));
            Assert.Equal(1, extractor.Disposed);
        }
        finally { await StopAsync(control, cancellation, search); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForPauseAsync(SearchExecutionControl control, Task search)
    {
        // Wait for acknowledgement, never infer absence of work from a fixed sleep duration.
        // Extractor/progress signals determine the event ordering; this only observes the gate.
        using var deadline = new CancellationTokenSource(Deadline);
        while (!control.IsPaused)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (search.IsCompleted)
            {
                await search;
                Assert.Fail("Search completed before acknowledging the requested pause.");
            }
            await Task.Yield();
        }
    }

    private static async Task StopAsync(SearchExecutionControl control, CancellationTokenSource cancellation, Task search)
    {
        cancellation.Cancel();
        control.Resume();
        await search.WaitAsync(Deadline);
    }

    private sealed class ProbeExtractor(string[] texts,
        Func<string, int, CancellationToken, Task>? onMoveNext = null) : ITextExtractor
    {
        private int _started;
        private int _moveNextCalls;
        private int _disposed;
        public IReadOnlyCollection<string> Extensions { get; } = [".fake"];
        public int Started => Volatile.Read(ref _started);
        public int MoveNextCalls => Volatile.Read(ref _moveNextCalls);
        public int Disposed => Volatile.Read(ref _disposed);

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _started);
            try
            {
                for (var index = 0; index <= texts.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _moveNextCalls);
                    if (onMoveNext is not null) await onMoveNext(path, index, cancellationToken);
                    if (index == texts.Length) yield break;
                    yield return new TextSegment($"segment:{index}", texts[index], new SourceLocation(index + 1, 1));
                }
            }
            finally { Interlocked.Increment(ref _disposed); }
        }
    }
}
