using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;

namespace LocalDocSearch.Benchmarks;

public sealed record ControlCounters(int ScannedFiles, int MatchedFiles, int Occurrences, int PublishedFiles,
    int PublishedOccurrences, int IssueDetails, int ProgressCallbacks);
public sealed record ControlMeasurement(string Mode, string Status, bool Requested, bool PauseAcknowledged,
    double? RequestAtMilliseconds, double? PauseAcknowledgementMilliseconds, double? StopMilliseconds,
    double? FirstResultMilliseconds, double ElapsedMilliseconds, double FreezeMilliseconds,
    bool CountersFrozen, bool SearchTaskEnded, ControlCounters? FrozenBefore, ControlCounters? FrozenAfter,
    ControlCounters FinalCounters, MemoryMeasurement Memory, double CpuMilliseconds,
    string ResultOrderSha256, string ResultContentSha256, bool Passed, IReadOnlyList<string> Mismatches);
public sealed record ControlBenchmarkReport(int SchemaVersion, string Tool, string Scenario, int Workers,
    string Profile, string BinarySha256, bool SourceIntegrity, string SourceSha256Before, string SourceSha256After,
    IReadOnlyList<ControlMeasurement> Rounds, bool Passed, IReadOnlyList<string> OmittedFields,
    string Trigger = "synchronous-progress-callback-after-first-scanned-file",
    string Coverage = "cooperative pause; engine-task completion includes its owned workers; synchronous parser is not preempted",
    string PeakWorkingSetSemantics = "process-lifetime cumulative high-water mark, not a per-round increment",
    string TimingSemantics = "control timings and allocations include actual-result hashing callbacks; performance run hashes in a separate excluded search");

public static class ControlBenchmarkRunner
{
    public const int FreezeMilliseconds = 200;
    public const int OpportunityTimeoutMilliseconds = 10_000;

    public static async Task<ControlBenchmarkReport> RunAsync(string root, string scenario, int workers = 1)
    {
        BenchmarkRunner.ValidateWorkers(workers);
        if (scenario is not ("many-small" or "mixed")) throw new BenchmarkException("unsupported-control-scenario");
        var manifest = BenchmarkCorpus.OpenVerified(root);
        string[] controls = BenchmarkRunner.ControlStamps(root);
        string before = SourceFingerprint(manifest, controls);
        var expected = manifest.Scenarios.Single(item => item.Scenario == scenario);
        var request = BenchmarkRunner.CreateRequest(BenchmarkCorpus.ScenarioRoot(root, expected), scenario);
        var rounds = new List<ControlMeasurement>();
        foreach (string mode in new[] { "pause-resume", "paused-stop", "running-stop" })
            rounds.Add(await MeasureAsync(request, expected, workers, mode).ConfigureAwait(false));
        BenchmarkCorpus.Verify(root, manifest);
        string[] controlsAfter = BenchmarkRunner.ControlStamps(root);
        if (!controls.SequenceEqual(controlsAfter)) throw new BenchmarkException("corpus-control-integrity-mismatch");
        string after = SourceFingerprint(manifest, controlsAfter);
        return new ControlBenchmarkReport(1, BenchmarkCorpus.Generator, scenario, workers, manifest.Profile,
            BenchmarkRunner.BinaryHash(), before == after, before, after, rounds.AsReadOnly(),
            before == after && rounds.All(item => item.Passed), BenchmarkRunner.OmittedFields);
    }

    // Fingerprints are built from all verified source SHA-256/length/mtime records plus actual control stamps.
    // Verification reads the files before and after; this does not re-extract or expose the source paths.
    internal static string SourceFingerprint(CorpusManifest manifest, string[] controls) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { manifest.Files, controls })));

    private static async Task<ControlMeasurement> MeasureAsync(SearchRequest request, ScenarioExpectation expected,
        int workers, string mode)
    {
        var control = new SearchExecutionControl();
        using var cancellation = new CancellationTokenSource();
        using var digest = new ResultDigest(request.RootFolder);
        using var process = Process.GetCurrentProcess();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        var clock = new Stopwatch();
        int scanned = 0, matched = 0, occurrences = 0, published = 0, publishedOccurrences = 0, issues = 0, callbacks = 0;
        bool requested = false;
        int? publishedAtStop = null, occurrencesAtStop = null;
        double? requestedAt = null, firstResult = null, stopAt = null, stopMilliseconds = null, acknowledgement = null;
        ControlCounters Snapshot()
        {
            lock (sync) return new(scanned, matched, occurrences, published, publishedOccurrences, issues, callbacks);
        }
        var progress = new InlineProgress<SearchProgress>(value =>
        {
            lock (sync)
            {
                scanned = value.ScannedFiles; matched = value.MatchedFiles; occurrences = value.Occurrences; callbacks++;
                if (!requested && value.ScannedFiles >= 1 && value.ScannedFiles < expected.Files)
                {
                    requested = true;
                    requestedAt = clock.Elapsed.TotalMilliseconds;
                    if (mode == "running-stop")
                    {
                        stopAt = requestedAt;
                        publishedAtStop = published; occurrencesAtStop = publishedOccurrences;
                        cancellation.Cancel();
                    }
                    else control.Pause();
                    signal.TrySetResult();
                }
            }
        });
        var results = new InlineProgress<FileResult>(value =>
        {
            lock (sync)
            {
                firstResult ??= clock.Elapsed.TotalMilliseconds;
                digest.Add(value);
                published++; publishedOccurrences += value.TotalOccurrences;
            }
        });
        var issueProgress = new InlineProgress<SearchIssue>(_ => { lock (sync) issues++; });
        var mismatches = new List<string>();
        long heapBefore = GC.GetTotalMemory(false), allocations = GC.GetTotalAllocatedBytes(true);
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        clock.Start();
        var task = new TextSearchEngine(DocumentExtractorCatalog.CreateDefault()).SearchAsync(request, progress, results,
            issueProgress, cancellation.Token, control, maxConcurrency: workers);
        ControlCounters? frozenBefore = null, frozenAfter = null;
        bool acknowledged = false, frozen = false;
        double freezeDuration = 0;
        try
        {
            Task opportunityTimeout = Task.Delay(OpportunityTimeoutMilliseconds);
            Task winner = await Task.WhenAny(signal.Task, task, opportunityTimeout).ConfigureAwait(false);
            if (!requested) mismatches.Add(winner == task ? "control-no-opportunity" : "control-request-timeout");
            else if (mode != "running-stop")
            {
                while (!control.IsPaused && !task.IsCompleted && clock.Elapsed.TotalMilliseconds - requestedAt!.Value < OpportunityTimeoutMilliseconds)
                    await Task.Delay(1).ConfigureAwait(false);
                if (control.IsPaused && !task.IsCompleted)
                {
                    acknowledged = true;
                    acknowledgement = clock.Elapsed.TotalMilliseconds - requestedAt!.Value;
                    frozenBefore = Snapshot();
                    double freezeStarted = clock.Elapsed.TotalMilliseconds;
                    await Task.Delay(FreezeMilliseconds).ConfigureAwait(false);
                    freezeDuration = clock.Elapsed.TotalMilliseconds - freezeStarted;
                    frozenAfter = Snapshot();
                    frozen = frozenBefore == frozenAfter && !task.IsCompleted && control.IsPaused;
                    if (!frozen) mismatches.Add("pause-counters-not-frozen");
                    if (mode == "paused-stop")
                    {
                        stopAt = clock.Elapsed.TotalMilliseconds;
                        cancellation.Cancel(); // deliberately no Resume: cancellation must release paused workers
                    }
                    else control.Resume();
                }
                else mismatches.Add(task.IsCompleted ? "pause-no-opportunity" : "pause-acknowledgement-timeout");
            }
        }
        finally
        {
            if (mismatches.Count > 0) { cancellation.Cancel(); control.Resume(); }
        }
        // Never leave the search or its workers detached. The outer measurement script also bounds process time.
        SearchSummary summary = await task.ConfigureAwait(false);
        clock.Stop();
        if (stopAt.HasValue) stopMilliseconds = clock.Elapsed.TotalMilliseconds - stopAt.Value;
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        var memory = new MemoryMeasurement(process.PeakWorkingSet64, process.WorkingSet64, heapBefore, GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.FragmentedBytes, GC.GetTotalAllocatedBytes(true) - allocations,
            new GcCounts(GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2));
        double cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var final = Snapshot();
        // Observe quiescence after task completion as well as completion itself.
        await Task.Delay(FreezeMilliseconds).ConfigureAwait(false);
        if (final != Snapshot()) mismatches.Add("callbacks-after-search-task-ended");
        if (mode == "pause-resume")
        {
            if (summary.Status.ToString() != expected.Status) mismatches.Add("resume-status");
            if (summary.Progress.ScannedFiles != expected.Files || summary.Progress.MatchedFiles != expected.MatchedFiles ||
                summary.Progress.Occurrences != expected.Occurrences || final.PublishedFiles != expected.MatchedFiles ||
                final.PublishedOccurrences != expected.Occurrences) mismatches.Add("resume-counts");
            if (summary.Diagnostics.ErrorCount != expected.Errors || summary.Diagnostics.WarningCount != expected.Warnings)
                mismatches.Add("resume-diagnostics");
        }
        else
        {
            if (summary.Status != SearchStatus.Cancelled) mismatches.Add("stop-status");
            if (!stopAt.HasValue) mismatches.Add("stop-not-requested");
            if (mode == "running-stop" && (final.PublishedFiles != publishedAtStop || final.PublishedOccurrences != occurrencesAtStop))
                mismatches.Add("results-after-running-stop");
            if (final.PublishedOccurrences != final.PublishedFiles * expected.MatchesPerPublishedFile)
                mismatches.Add("stop-whole-file-publication");
            if (mode == "paused-stop" && frozenAfter is not null &&
                (final.PublishedFiles != frozenAfter.PublishedFiles || final.PublishedOccurrences != frozenAfter.PublishedOccurrences))
                mismatches.Add("results-after-paused-stop");
        }
        if (!requested) mismatches.Add("control-not-requested");
        return new ControlMeasurement(mode, summary.Status.ToString(), requested, acknowledged, requestedAt, acknowledgement,
            stopMilliseconds, firstResult, clock.Elapsed.TotalMilliseconds, freezeDuration, frozen, task.IsCompleted,
            frozenBefore, frozenAfter, final, memory, cpuMilliseconds, digest.OrderSha256(), digest.ContentSha256(),
            mismatches.Count == 0, mismatches.AsReadOnly());
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
