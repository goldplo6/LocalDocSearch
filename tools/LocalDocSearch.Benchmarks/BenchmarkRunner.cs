using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;

namespace LocalDocSearch.Benchmarks;

public sealed record MachineInfo(string Runtime, string OperatingSystem, string ProcessArchitecture,
    string OperatingSystemArchitecture, int LogicalProcessors, long TotalAvailableManagedMemoryBytes,
    long StopwatchFrequency, bool ServerGc);

public sealed record GcCounts(int Generation0, int Generation1, int Generation2);
public sealed record MemoryMeasurement(long ProcessPeakWorkingSetBytesLifetime, long WorkingSetBytesAtEnd,
    long ManagedHeapBytesBefore, long ManagedHeapBytesAfter, long ManagedHeapSizeBytesAtLastGc,
    long ManagedFragmentedBytesAtLastGc, long TotalAllocatedBytesDelta, GcCounts CollectionsDelta);
public sealed record CancellationMeasurement(string Trigger, bool Requested, double? RequestedAtMilliseconds,
    double? RequestToCompletionMilliseconds, int ResultsAfterRequest, bool TaskEnded,
    string Coverage = "file-boundary; parser-preemption-not-measured");
public sealed record RoundMeasurement(int Round, string Phase, string Status, double ElapsedMilliseconds,
    double? FirstResultMilliseconds, int ScannedFiles, int MatchedFiles, int Occurrences, int PublishedFiles,
    int PublishedOccurrences, int? MinimumMatchesPerPublishedFile, int? MaximumMatchesPerPublishedFile,
    long ErrorCount, long WarningCount, long SuppressedIssueCount, int IssueDetailsObserved,
    string? LimitReasonCode, MemoryMeasurement Memory, CancellationMeasurement? Cancellation,
    bool Passed, IReadOnlyList<string> Mismatches, string? ResultOrderSha256 = null,
    string? ResultContentSha256 = null, double CpuMilliseconds = 0);
public sealed record Distribution(int Count, double Minimum, double Median, double Maximum);
public sealed record IntegrityMeasurement(bool VerifiedBeforeAndAfter, double BeforeMilliseconds, double AfterMilliseconds,
    bool ExcludedFromSearchTiming = true, bool ReadsCanWarmOperatingSystemCache = true,
    string? SourceSha256Before = null, string? SourceSha256After = null);
public sealed record BenchmarkReport(int SchemaVersion, string Tool, string Scenario, string Profile, DateTime StartedUtc,
    MachineInfo Machine, ScenarioExpectation Expected, IReadOnlyList<RoundMeasurement> Rounds,
    Distribution FirstRoundElapsed, Distribution? RepeatedElapsed, Distribution? FirstResultElapsed,
    IntegrityMeasurement Integrity, bool Passed, IReadOnlyList<string> OmittedFields,
    string CacheSemantics = "OS file cache was not cleared; first-round and repeated in-process searches only",
    string PeakWorkingSetSemantics = "process-lifetime cumulative high-water mark, not a per-round increment",
    string AllocationSemantics = "process-wide GC.GetTotalAllocatedBytes(precise:true) delta; no forced collection",
    int Workers = 1, string? BinarySha256 = null, RoundMeasurement? ResultVerification = null,
    string ResultVerificationSemantics = "separate same-workers search after performance rounds; hashing excluded from timing/allocation distributions");

public static class BenchmarkRunner
{
    public const int MaximumRepeats = 20;
    public static readonly IReadOnlyList<string> OmittedFields = Array.AsReadOnly(new[]
        { "query", "terms", "body", "paths", "fileNames", "issueMessages", "machineName", "userName" });

    public static async Task<BenchmarkReport> RunAsync(string root, string scenarioName, int repeat = 3, int workers = 1)
    {
        ValidateWorkers(workers);
        if (repeat is < 1 or > MaximumRepeats) throw new BenchmarkException("invalid-repeat");
        if (!BenchmarkCorpus.ScenarioNames.Contains(scenarioName, StringComparer.Ordinal))
            throw new BenchmarkException("unknown-scenario");
        DateTime startedUtc = DateTime.UtcNow;
        var validation = Stopwatch.StartNew();
        var manifest = BenchmarkCorpus.OpenVerified(root);
        var controlsBefore = ControlStamps(root);
        string sourceBefore = ControlBenchmarkRunner.SourceFingerprint(manifest, controlsBefore);
        validation.Stop();
        double beforeMilliseconds = validation.Elapsed.TotalMilliseconds;
        var expected = manifest.Scenarios.Single(item => item.Scenario == scenarioName);
        var engine = new TextSearchEngine(DocumentExtractorCatalog.CreateDefault());
        var request = CreateRequest(BenchmarkCorpus.ScenarioRoot(root, expected), scenarioName);
        var rounds = new List<RoundMeasurement>(repeat);
        for (int index = 0; index < repeat; index++)
            rounds.Add(await MeasureAsync(engine, request, expected, index + 1, workers).ConfigureAwait(false));
        var verificationRound = await MeasureAsync(engine, request, expected, 0, workers, verifyResults: true).ConfigureAwait(false);
        validation.Restart();
        BenchmarkCorpus.Verify(root, manifest);
        string[] controlsAfter = ControlStamps(root);
        if (!controlsBefore.SequenceEqual(controlsAfter)) throw new BenchmarkException("corpus-control-integrity-mismatch");
        string sourceAfter = ControlBenchmarkRunner.SourceFingerprint(manifest, controlsAfter);
        validation.Stop();
        return new BenchmarkReport(1, BenchmarkCorpus.Generator, scenarioName, manifest.Profile, startedUtc,
            Machine(), expected, rounds.AsReadOnly(), Summarize([rounds[0].ElapsedMilliseconds])!,
            Summarize(rounds.Skip(1).Select(round => round.ElapsedMilliseconds)),
            Summarize(rounds.Where(round => round.FirstResultMilliseconds.HasValue).Select(round => round.FirstResultMilliseconds!.Value)),
            new IntegrityMeasurement(true, beforeMilliseconds, validation.Elapsed.TotalMilliseconds,
                SourceSha256Before: sourceBefore, SourceSha256After: sourceAfter),
            verificationRound.Passed && rounds.All(round => round.Passed), OmittedFields, Workers: workers,
            BinarySha256: BinaryHash(), ResultVerification: verificationRound);
    }

    internal static void ValidateWorkers(int workers)
    {
        if (workers is not (1 or 2 or 4)) throw new BenchmarkException("invalid-workers");
    }

    internal static string BinaryHash()
    {
        using var input = File.OpenRead(typeof(BenchmarkRunner).Assembly.Location);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
    }

    internal static SearchRequest CreateRequest(string scenarioRoot, string scenarioName)
    {
        string[] terms = scenarioName switch
        {
            "many-small" or "mixed" or "cancel" => ["西瓜", "苹果"],
            "literal-and" => ["西瓜", ".*"],
            _ => ["西瓜"]
        };
        return new SearchRequest(scenarioRoot, terms, SearchOperator.And, ContextCharacters: 0);
    }

    private static async Task<RoundMeasurement> MeasureAsync(TextSearchEngine engine, SearchRequest request,
        ScenarioExpectation expected, int roundNumber, int workers, bool verifyResults = false)
    {
        using var digest = verifyResults ? new ResultDigest(request.RootFolder) : null;
        using var process = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        int publishedFiles = 0, publishedOccurrences = 0, details = 0, resultsAfterRequest = 0;
        int? minimum = null, maximum = null;
        double? firstResult = null, requestedAt = null;
        bool cancellationRequested = false;
        var clock = new Stopwatch();
        var results = new InlineProgress<FileResult>(result =>
        {
            if (cancellationRequested) resultsAfterRequest++;
            firstResult ??= clock.Elapsed.TotalMilliseconds;
            digest?.Add(result);
            publishedFiles++;
            publishedOccurrences += result.TotalOccurrences;
            minimum = Math.Min(minimum ?? int.MaxValue, result.TotalOccurrences);
            maximum = Math.Max(maximum ?? 0, result.TotalOccurrences);
            if (expected.Cancellation && !cancellationRequested)
            {
                requestedAt = clock.Elapsed.TotalMilliseconds;
                cancellationRequested = true;
                cancellation.Cancel();
            }
        });
        var issues = new InlineProgress<SearchIssue>(_ => details++); // never keep paths or messages
        long heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        clock.Start();
        var task = engine.SearchAsync(request, results: results, issues: issues, cancellationToken: cancellation.Token,
            maxConcurrency: workers);
        SearchSummary summary = await task.ConfigureAwait(false);
        clock.Stop();
        long allocationAfter = GC.GetTotalAllocatedBytes(precise: true);
        var collections = new GcCounts(GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2);
        long heapAfter = GC.GetTotalMemory(forceFullCollection: false);
        var info = GC.GetGCMemoryInfo();
        process.Refresh();
        double cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var memory = new MemoryMeasurement(process.PeakWorkingSet64, process.WorkingSet64, heapBefore, heapAfter,
            info.HeapSizeBytes, info.FragmentedBytes, allocationAfter - allocationBefore, collections);
        var diagnostics = summary.Diagnostics;
        string status = summary.Status.ToString();
        var mismatches = new List<string>();
        void Check(bool condition, string code) { if (!condition) mismatches.Add(code); }
        Check(status == expected.Status, "status");
        Check(summary.Progress.MatchedFiles == expected.MatchedFiles, "matched-files");
        Check(summary.Progress.Occurrences == expected.Occurrences, "occurrences");
        Check(publishedFiles == expected.MatchedFiles, "published-files");
        Check(publishedOccurrences == expected.Occurrences, "published-occurrences");
        Check(minimum == expected.MatchesPerPublishedFile && maximum == expected.MatchesPerPublishedFile, "whole-file-publication");
        Check(diagnostics.ErrorCount == expected.Errors, "error-count");
        Check(diagnostics.WarningCount == expected.Warnings, "warning-count");
        Check(details <= 1001, "issue-detail-cap");
        if (!expected.Cancellation && expected.Status != "Limited")
            Check(summary.Progress.ScannedFiles == expected.Files, "scanned-files");
        if (expected.Scenario == "global-budget")
        {
            Check(summary.Progress.Occurrences <= 200_000, "global-match-cap");
            Check(!string.IsNullOrWhiteSpace(diagnostics.LimitReason), "limit-reason-missing");
        }
        if (expected.Errors + expected.Warnings > 1000)
        {
            Check(diagnostics.SuppressedIssueCount > 0, "suppressed-issue-count");
            // Detailed callback messages may include one synthetic suppression notice.
            Check(diagnostics.SuppressedIssueCount >= expected.Errors + expected.Warnings - details, "suppressed-issue-total");
        }
        CancellationMeasurement? cancelled = null;
        if (expected.Cancellation)
        {
            Check(cancellationRequested, "cancellation-not-requested");
            Check(resultsAfterRequest == 0, "results-after-cancellation");
            Check(task.IsCompleted, "search-task-not-ended");
            cancelled = new CancellationMeasurement("synchronous-first-file-result-callback", cancellationRequested,
                requestedAt, requestedAt.HasValue ? clock.Elapsed.TotalMilliseconds - requestedAt.Value : null,
                resultsAfterRequest, task.IsCompleted);
        }
        return new RoundMeasurement(roundNumber, verifyResults ? "verification-excluded" : roundNumber == 1 ? "first-round" : "repeat", status,
            clock.Elapsed.TotalMilliseconds, firstResult, summary.Progress.ScannedFiles, summary.Progress.MatchedFiles,
            summary.Progress.Occurrences, publishedFiles, publishedOccurrences, minimum, maximum,
            diagnostics.ErrorCount, diagnostics.WarningCount, diagnostics.SuppressedIssueCount, details,
            // LimitReason is free text from Core. Expose a fixed code, never the original text.
            string.IsNullOrWhiteSpace(diagnostics.LimitReason) ? null : "resource-limit", memory, cancelled,
            mismatches.Count == 0, mismatches.AsReadOnly(), digest?.OrderSha256(), digest?.ContentSha256(), cpuMilliseconds);
    }

    private static MachineInfo Machine() => new(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(),
        Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Stopwatch.Frequency,
        System.Runtime.GCSettings.IsServerGC);

    private static Distribution? Summarize(IEnumerable<double> measurements)
    {
        double[] values = measurements.Order().ToArray();
        if (values.Length == 0) return null;
        int middle = values.Length / 2;
        return new Distribution(values.Length, values[0], values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2 : values[middle], values[^1]);
    }

    internal static string[] ControlStamps(string root) => new[] { BenchmarkCorpus.MarkerName, BenchmarkCorpus.ManifestName }
        .Select(name =>
        {
            string path = Path.Combine(root, name);
            using var stream = File.OpenRead(path);
            return File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }).ToArray();

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
