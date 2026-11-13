using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Benchmarks;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

[Collection(CorpusCollection.Name)]
public sealed class ConcurrencyBenchmarkTests(OriginalCorpusFixture fixture)
{
    [Theory]
    [InlineData("many-small")]
    [InlineData("mixed")]
    [InlineData("dense-file")]
    [InlineData("global-budget")]
    [InlineData("errors")]
    [InlineData("literal-and")]
    [InlineData("cancel")]
    public async Task WorkerCountsPreserveActualResultContentAndPublicationOrder(string scenario)
    {
        var serial = await BenchmarkRunner.RunAsync(fixture.Root, scenario, repeat: 1);
        Assert.Equal(1, serial.Workers);
        Assert.Equal(1, serial.SchemaVersion);
        var baseline = Assert.Single(serial.Rounds);
        Assert.True(serial.Passed, string.Join(",", baseline.Mismatches));
        Assert.NotNull(serial.ResultVerification);
        Assert.Null(baseline.ResultContentSha256);
        foreach (int workers in new[] { 2, 4 })
        {
            var report = await BenchmarkRunner.RunAsync(fixture.Root, scenario, repeat: 1, workers: workers);
            var round = Assert.Single(report.Rounds);
            Assert.True(report.Passed, string.Join(",", round.Mismatches));
            Assert.Equal(workers, report.Workers);
            Assert.Equal(serial.BinarySha256, report.BinarySha256);
            Assert.Equal(serial.Integrity.SourceSha256Before, report.Integrity.SourceSha256After);
            Assert.NotNull(report.ResultVerification);
            Assert.True(report.ResultVerification.Passed);
            Assert.Equal("verification-excluded", report.ResultVerification.Phase);
            Assert.Equal(serial.ResultVerification.ResultOrderSha256, report.ResultVerification.ResultOrderSha256);
            Assert.Equal(serial.ResultVerification.ResultContentSha256, report.ResultVerification.ResultContentSha256);
            Assert.Null(round.ResultContentSha256);
            Assert.Equal(baseline.Status, round.Status);
            Assert.Equal(baseline.PublishedFiles, round.PublishedFiles);
            Assert.Equal(baseline.PublishedOccurrences, round.PublishedOccurrences);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("8")]
    [InlineData("-1")]
    public async Task InvalidWorkerOptionsFailBeforeCorpusIo(string workers)
    {
        using var output = new StringWriter();
        int exit = await BenchmarkCli.ExecuteAsync(["run", "private-nonexistent-root", "many-small", "--workers", workers], output);
        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-workers", document.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("private-nonexistent-root", output.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkRunner.RunAsync("private-nonexistent-root", "many-small", workers: int.Parse(workers)));
    }

    [Fact]
    public void DigestsObserveAllActualMatchFieldsAndDistinguishOrderFromContent()
    {
        string root = Path.Combine(Path.GetTempPath(), "digest-test-root");
        var location = new SourceLocator(SourceKind.ExcelCell, Sheet: "sheet-a", Address: "B2", ValueKind: "text");
        var match = new MatchItem("term", "before", "actual", "after", location, "segment-a", 7);
        var first = new FileResult(Path.Combine(root, "a.txt"), ".txt", DateTime.UnixEpoch, [match]);
        var second = first with { FullPath = Path.Combine(root, "b.txt") };
        using var digest = new ResultDigest(root);
        digest.Add(first); digest.Add(second);
        using var reverse = new ResultDigest(root);
        reverse.Add(second); reverse.Add(first);
        Assert.NotEqual(digest.OrderSha256(), reverse.OrderSha256());
        Assert.Equal(digest.ContentSha256(), reverse.ContentSha256());
        var variants = new[] {
            first with { Extension = ".csv" }, first with { LastWriteTimeUtc = DateTime.UnixEpoch.AddTicks(1) },
            first with { ExtractionWarnings = ["warning"] },
            first with { Matches = [match with { Term = "other" }] },
            first with { Matches = [match with { Before = "other" }] },
            first with { Matches = [match with { Text = "other" }] },
            first with { Matches = [match with { After = "other" }] },
            first with { Matches = [match with { SourceSegmentId = "other" }] },
            first with { Matches = [match with { Offset = 8 }] },
            first with { Matches = [match with { Location = location with { Address = "C3" } }] }
        };
        foreach (var variant in variants)
        {
            using var changed = new ResultDigest(root);
            changed.Add(variant); changed.Add(second);
            Assert.NotEqual(digest.ContentSha256(), changed.ContentSha256());
        }
        using var relocated = new ResultDigest(Path.Combine(root, "relocated"));
        relocated.Add(first with { FullPath = Path.Combine(root, "relocated", "a.txt") });
        relocated.Add(second with { FullPath = Path.Combine(root, "relocated", "b.txt") });
        Assert.Equal(digest.ContentSha256(), relocated.ContentSha256());
    }

    [Theory]
    [InlineData(1, "pause-resume")]
    [InlineData(2, "pause-resume")]
    [InlineData(4, "pause-resume")]
    [InlineData(1, "paused-stop")]
    [InlineData(2, "paused-stop")]
    [InlineData(4, "paused-stop")]
    [InlineData(1, "running-stop")]
    [InlineData(2, "running-stop")]
    [InlineData(4, "running-stop")]
    public async Task DeterministicControlDrainsEveryExtractorAndPreservesSource(int workers, string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "LocalDocSearch.ControlBenchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < 16; i++) File.WriteAllText(Path.Combine(root, $"{i:D3}.bench"), "unchanged-source");
        string before = SourceHash(root);
        var extractor = new GatedExtractor();
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int published = 0;
        var progress = new InlineProgress<FileResult>(_ =>
        {
            if (Interlocked.Increment(ref published) != 1) return;
            Volatile.Write(ref extractor.BlockFollowers, 1);
            if (mode == "running-stop") cancellation.Cancel(); else control.Pause();
            requested.TrySetResult();
        });
        var search = new TextSearchEngine([extractor]).SearchAsync(new SearchRequest(root, ["term"]),
            results: progress, cancellationToken: cancellation.Token, executionControl: control, maxConcurrency: workers);
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (mode != "running-stop")
            {
                extractor.Release.TrySetResult(); // let in-flight MoveNext reach the engine pause checkpoint
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!control.IsPaused && !search.IsCompleted) await Task.Delay(1, timeout.Token);
                Assert.True(control.IsPaused);
                Assert.False(search.IsCompleted);
                int frozen = Volatile.Read(ref published);
                await Task.Delay(200);
                Assert.Equal(frozen, Volatile.Read(ref published));
                Assert.False(search.IsCompleted);
                if (mode == "paused-stop") cancellation.Cancel(); else control.Resume();
            }
            var summary = await search.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(mode == "pause-resume" ? SearchStatus.Completed : SearchStatus.Cancelled, summary.Status);
            Assert.Equal(mode == "pause-resume" ? 16 : 1, Volatile.Read(ref published));
            Assert.Equal(0, Volatile.Read(ref extractor.Active));
            Assert.Equal(Volatile.Read(ref extractor.Started), Volatile.Read(ref extractor.Ended));
            int ended = Volatile.Read(ref published);
            await Task.Delay(200);
            Assert.Equal(ended, Volatile.Read(ref published));
            Assert.Equal(before, SourceHash(root));
        }
        finally
        {
            cancellation.Cancel(); control.Resume(); extractor.Release.TrySetResult();
            await search.WaitAsync(TimeSpan.FromSeconds(10));
            // Unique directory created by this test; wait for all owned work before deleting.
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SourceHash(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory.GetFiles(root).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class GatedExtractor : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions => [".bench"];
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Started, Ended, Active, BlockFollowers;
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Started);
            Interlocked.Increment(ref Active);
            try
            {
                if (Volatile.Read(ref BlockFollowers) != 0) await Release.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TextSegment("segment", "term", new SourceLocation(1, 1));
            }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Ended); }
        }
    }
}
