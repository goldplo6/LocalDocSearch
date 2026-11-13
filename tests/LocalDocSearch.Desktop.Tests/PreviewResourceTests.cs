using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop;
using LocalDocSearch.Extractors;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

[CollectionDefinition("Preview resource serialization", DisableParallelization = true)]
public sealed class PreviewResourceCollection { }

[Collection("Preview resource serialization")]
public sealed class PreviewResourceTests
{
    private const string SourceText = "甲西瓜乙";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DifferentLoaderInstancesDoNotOverlapExtraction()
    {
        using var files = new SourceFiles();
        var firstExtractor = new ControlledExtractor();
        var secondExtractor = new ControlledExtractor();
        var first = new MatchPreviewLoader([firstExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
        Task<MatchPreviewData>? second = null;
        try
        {
            await AwaitAsync(firstExtractor.Started.Task);
            second = new MatchPreviewLoader([secondExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, secondExtractor.StartCount);

            firstExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(first));
            await AwaitAsync(secondExtractor.Started.Task);
            Assert.True(firstExtractor.Finished.Task.IsCompleted);
            secondExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(second));
            Assert.Equal(1, firstExtractor.StartCount);
            Assert.Equal(1, secondExtractor.StartCount);
        }
        finally
        {
            firstExtractor.Release.TrySetResult();
            secondExtractor.Release.TrySetResult();
            await DrainAsync(first);
            await DrainAsync(second);
        }
    }

    [Fact]
    public async Task CanceledWaiterDoesNotStartExtraction()
    {
        using var files = new SourceFiles();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstExtractor = new ControlledExtractor();
        var canceledExtractor = new ControlledExtractor();
        var first = new MatchPreviewLoader([firstExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
        Task<MatchPreviewData>? waiting = null;
        try
        {
            await AwaitAsync(firstExtractor.Started.Task);
            waiting = new MatchPreviewLoader([canceledExtractor]).LoadAsync(files.Request(), cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(waiting));
            Assert.Equal(0, canceledExtractor.StartCount);
            Assert.False(canceledExtractor.Started.Task.IsCompleted);
            Assert.False(first.IsCompleted);

            firstExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(first));
            Assert.Equal(0, canceledExtractor.StartCount);
        }
        finally
        {
            firstExtractor.Release.TrySetResult();
            canceledExtractor.Release.TrySetResult();
            await DrainAsync(first);
            await DrainAsync(waiting);
        }
    }

    [Fact]
    public async Task ExtractionFailureReleasesGateForWaitingLoader()
    {
        using var files = new SourceFiles();
        var failingExtractor = new ControlledExtractor(fail: true);
        var nextExtractor = new ControlledExtractor();
        var failed = new MatchPreviewLoader([failingExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
        Task<MatchPreviewData>? next = null;
        try
        {
            await AwaitAsync(failingExtractor.Started.Task);
            next = new MatchPreviewLoader([nextExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
            Assert.Equal(0, nextExtractor.StartCount);
            failingExtractor.Release.TrySetResult();
            await Assert.ThrowsAsync<ExtractionException>(() => AwaitAsync(failed));
            await AwaitAsync(nextExtractor.Started.Task);
            Assert.True(failingExtractor.Finished.Task.IsCompleted);
            nextExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(next));
        }
        finally
        {
            failingExtractor.Release.TrySetResult();
            nextExtractor.Release.TrySetResult();
            await DrainAsync(failed);
            await DrainAsync(next);
        }
    }

    [Fact]
    public async Task CanceledSynchronousParserKeepsGateUntilItReturnsThenNextLoaderRuns()
    {
        using var files = new SourceFiles();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var blockedExtractor = new ControlledExtractor(synchronous: true);
        var nextExtractor = new ControlledExtractor();
        var request = files.Request();
        var blocked = Task.Run(() => new MatchPreviewLoader([blockedExtractor]).LoadAsync(request, cancellation.Token));
        Task<MatchPreviewData>? next = null;
        try
        {
            await AwaitAsync(blockedExtractor.Started.Task);
            cancellation.Cancel();
            next = new MatchPreviewLoader([nextExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
            Assert.False(blocked.IsCompleted);
            Assert.False(blockedExtractor.Finished.Task.IsCompleted);
            Assert.False(next.IsCompleted);
            Assert.Equal(0, nextExtractor.StartCount);

            blockedExtractor.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitAsync(blocked));
            await AwaitAsync(nextExtractor.Started.Task);
            Assert.True(blockedExtractor.Finished.Task.IsCompleted);
            nextExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(next));
        }
        finally
        {
            blockedExtractor.Release.TrySetResult();
            nextExtractor.Release.TrySetResult();
            await DrainAsync(blocked);
            await DrainAsync(next);
        }
    }

    [Fact]
    public async Task QueuedPreviewChecksSourceStateAfterEnteringGateBeforeExtraction()
    {
        using var files = new SourceFiles();
        var firstExtractor = new ControlledExtractor();
        var queuedExtractor = new ControlledExtractor();
        var queuedRequest = files.Request();
        var first = new MatchPreviewLoader([firstExtractor]).LoadAsync(files.Request(), TestContext.Current.CancellationToken);
        Task<MatchPreviewData>? queued = null;
        try
        {
            await AwaitAsync(firstExtractor.Started.Task);
            queued = new MatchPreviewLoader([queuedExtractor]).LoadAsync(queuedRequest, TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            Assert.Equal(0, queuedExtractor.StartCount);
            File.SetLastWriteTimeUtc(queuedRequest.FullPath, queuedRequest.LastWriteTimeUtc.AddSeconds(5));
            Assert.NotEqual(queuedRequest.LastWriteTimeUtc, File.GetLastWriteTimeUtc(queuedRequest.FullPath));

            firstExtractor.Release.TrySetResult();
            AssertSelectedOccurrence(await AwaitAsync(first));
            // Release preemptively so a regression that extracts stale state also finishes deterministically.
            queuedExtractor.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => AwaitAsync(queued));
            Assert.Equal(0, queuedExtractor.StartCount);
        }
        finally
        {
            firstExtractor.Release.TrySetResult();
            queuedExtractor.Release.TrySetResult();
            await DrainAsync(first);
            await DrainAsync(queued);
        }
    }

    private static void AssertSelectedOccurrence(MatchPreviewData data)
    {
        Assert.Equal(SourceText, data.Text);
        Assert.Equal(1, data.SelectionStart);
        Assert.Equal(2, data.SelectionLength);
        Assert.Equal("西瓜", data.Text.Substring(data.SelectionStart, data.SelectionLength));
        Assert.False(data.IsClipped);
    }

    private static Task AwaitAsync(Task task) => task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    private static Task<T> AwaitAsync<T>(Task<T> task) => task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

    private static async Task DrainAsync(Task? task)
    {
        if (task is null) return;
        try { await task.WaitAsync(Timeout); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private sealed class ControlledExtractor(bool synchronous = false, bool fail = false) : ITextExtractor
    {
        private int _startCount;
        public int StartCount => Volatile.Read(ref _startCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> Extensions => [".txt"];

        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            Started.TrySetResult();
            try
            {
                // Deliberately ignore cancellation while simulating an uninterruptible third-party parser.
                if (synchronous) Release.Task.GetAwaiter().GetResult();
                else await Release.Task;
                if (fail) throw new ExtractionException("controlled preview extraction failure");
                yield return new TextSegment("line:1", SourceText, new SourceLocation(1, 1));
            }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class SourceFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalDocSearch-preview-resources-" + Guid.NewGuid().ToString("N"));
        public SourceFiles() => Directory.CreateDirectory(_root);
        public MatchPreviewRequest Request()
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, SourceText);
            return new(path, new MatchItem("西瓜", "甲", "西瓜", "乙", new SourceLocation(1, 2), "line:1", 1),
                File.GetLastWriteTimeUtc(path), TextEncodingMode.StrictUnicode, new DocumentScopeOptions());
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
