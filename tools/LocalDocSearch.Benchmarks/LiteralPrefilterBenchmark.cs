using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using Microsoft.Win32.SafeHandles;

namespace LocalDocSearch.Benchmarks;

public sealed record LiteralManifest(string Generator, bool Quick, IReadOnlyList<CorpusFile> Files);
public sealed record LiteralReport(string Scenario, int Workers, MachineInfo Machine, ScenarioExpectation Expected,
    IReadOnlyList<RoundMeasurement> Rounds, RoundMeasurement ResultVerification, string CoreSha256, string DriverSha256,
    string SourceSha256Before, string SourceSha256After, bool Passed,
    string TimingSemantics = "request/query creation included; separate verification hashing and before/after integrity excluded; OS cache not cleared",
    string PeakSemantics = "process lifetime cumulative peak; allocations are process-wide GC delta",
    string QuerySemantics = "32/64-term sparse/all queries retain only 16 present tokens; unicode scenarios use Chinese text and terms");

/// <summary>Independent original corpus and fixed serial experiments; never repairs or reuses a directory.</summary>
public static class LiteralPrefilterBenchmark
{
    public const string Marker = ".literal-prefilter-root", Manifest = "literal-manifest.json";
    public const string Generator = "LocalDocSearch/literal-prefilter-v1";
    public static IReadOnlyList<string> Scenarios { get; } = Array.AsReadOnly(new[]
        { "none-4", "none-16", "none-64", "sparse-16", "all-16", "single-long", "short-16", "regex",
            "none-32", "sparse-32", "all-32", "sparse-64", "all-64", "unicode-none-64", "unicode-sparse-64" });
    private static readonly string[] Kinds = ["none", "sparse", "all", "short", "unicode"];
    private static string Term(int index) => $"zzneedle{index:D3}zz";
    private static string UnicodeTerm(int index) => $"词条{index:D3}终";
    private static string Body(string kind, bool quick)
    {
        int count = (kind == "short" ? 16384 : 256) / (quick ? 32 : 1);
        bool unicode = kind == "unicode";
        int length = kind == "short" ? 127 : unicode ? 2730 : 8191;
        string tokens = string.Join(' ', Enumerable.Range(0, 16).Select(index => unicode ? UnicodeTerm(index) : Term(index)));
        var body = new StringBuilder(count * (length + 1));
        for (int i = 0; i < count; i++)
        {
            string suffix = kind == "all" || (kind == "sparse" || unicode) && i == 0 ? tokens : "";
            body.Append(unicode ? '文' : 'x', length - suffix.Length).Append(suffix);
            if (unicode) body.Append('x', 8191 - (length - suffix.Length) * 3 - Encoding.UTF8.GetByteCount(suffix));
            body.Append('\n');
        }
        return body.ToString();
    }
    public static LiteralManifest Prepare(string root, bool quick = false)
    {
        root = Root(root);
        if (File.Exists(root) || Directory.Exists(root)) throw new BenchmarkException("literal-root-must-be-new");
        Directory.CreateDirectory(root);
        WriteNew(Path.Combine(root, Marker), Generator);
        var files = new List<CorpusFile>();
        foreach (string kind in Kinds)
        {
            string directory = Path.Combine(root, kind);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "original.txt");
            WriteNew(path, Body(kind, quick));
            var info = new FileInfo(path);
            files.Add(new CorpusFile(kind + "/original.txt", kind, info.Length, info.LastWriteTimeUtc.Ticks, Hash(path), null));
        }
        var manifest = new LiteralManifest(Generator, quick, files.AsReadOnly());
        WriteNew(Path.Combine(root, Manifest), BenchmarkJson.Serialize(manifest));
        OpenVerified(root);
        return manifest;
    }
    public static LiteralManifest OpenVerified(string root)
    {
        root = Root(root);
        string manifestPath = Path.Combine(root, Manifest), markerPath = Path.Combine(root, Marker);
        Ordinary(manifestPath); Ordinary(markerPath);
        if (new FileInfo(manifestPath).Length > 16384 || new FileInfo(markerPath).Length > 128 ||
            File.ReadAllText(markerPath) != Generator) throw new BenchmarkException("literal-invalid-marker");
        LiteralManifest manifest;
        try { manifest = JsonSerializer.Deserialize<LiteralManifest>(File.ReadAllText(manifestPath), BenchmarkJson.Options)!; }
        catch (JsonException) { throw new BenchmarkException("literal-invalid-manifest"); }
        if (manifest is null || manifest.Generator != Generator || manifest.Files is null || manifest.Files.Count != Kinds.Length)
            throw new BenchmarkException("literal-invalid-manifest");
        var entries = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!entries.SetEquals(Kinds.Concat([Marker, Manifest]))) throw new BenchmarkException("literal-unowned-entry");
        foreach (string kind in Kinds)
        {
            string directory = Path.Combine(root, kind);
            BenchmarkCorpus.AssertOrdinaryAncestors(directory);
            var selected = manifest.Files.Where(file => file is not null && file.Scenario == kind).ToArray();
            if (selected.Length != 1) throw new BenchmarkException("literal-invalid-manifest");
            var file = selected[0];
            if (file.RelativePath != kind + "/original.txt") throw new BenchmarkException("invalid-manifest-path");
            string path = BenchmarkCorpus.ResolveOwnedPath(root, file.RelativePath);
            Ordinary(path);
            if (!Directory.GetFileSystemEntries(directory).SequenceEqual([path])) throw new BenchmarkException("literal-unowned-entry");
            var info = new FileInfo(path);
            byte[] original = Encoding.UTF8.GetBytes(Body(kind, manifest.Quick));
            if (file.Bytes != original.Length || file.Sha256 != Convert.ToHexString(SHA256.HashData(original)) ||
                info.Length != file.Bytes || info.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtcTicks || Hash(path) != file.Sha256)
                throw new BenchmarkException("literal-integrity-mismatch");
        }
        return manifest;
    }
    public static ScenarioExpectation Expected(string scenario, bool quick)
    {
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal)) throw new BenchmarkException("unknown-scenario");
        string kind = scenario.StartsWith("unicode", StringComparison.Ordinal) ? "unicode" :
            scenario.StartsWith("sparse", StringComparison.Ordinal) ? "sparse" :
            scenario.StartsWith("all", StringComparison.Ordinal) ? "all" : scenario == "short-16" ? "short" : "none";
        int hits = kind == "sparse" || scenario == "unicode-sparse-64" ? 16 : kind == "all" ? 16 * (quick ? 8 : 256) : 0;
        return new ScenarioExpectation(scenario, kind, 1, 2 * 1024 * 1024 / (quick ? 32 : 1), null,
            hits > 0 ? 1 : 0, hits, "Completed", 0, 0, hits, false);
    }
    public static async Task<LiteralReport> RunAsync(string root, string scenario, int repeat = 3)
    {
        if (repeat is < 1 or > 20) throw new BenchmarkException("invalid-repeat");
        Expected(scenario, true); // validate arguments before any corpus read
        root = Root(root);
        var manifest = OpenVerified(root);
        string before = Stamp(root, manifest);
        var expected = Expected(scenario, manifest.Quick);
        var rounds = new List<RoundMeasurement>();
        for (int i = 1; i <= repeat; i++) rounds.Add(await Measure(root, expected, i, false).ConfigureAwait(false));
        var verification = await Measure(root, expected, 0, true).ConfigureAwait(false);
        var afterManifest = OpenVerified(root);
        string after = Stamp(root, afterManifest);
        if (before != after) throw new BenchmarkException("literal-control-integrity-mismatch");
        var machine = new MachineInfo(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Stopwatch.Frequency,
            System.Runtime.GCSettings.IsServerGC);
        return new(scenario, 1, machine, expected, rounds.AsReadOnly(), verification,
            Hash(typeof(TextSearchEngine).Assembly.Location), Hash(typeof(LiteralPrefilterBenchmark).Assembly.Location),
            before, after, rounds.All(round => round.Passed) && verification.Passed);
    }
    private static async Task<RoundMeasurement> Measure(string root, ScenarioExpectation expected, int number, bool verify)
    {
        string directory = Path.Combine(root, expected.CorpusScenario);
        using var digest = verify ? new ResultDigest(directory) : null;
        using var process = Process.GetCurrentProcess();
        var clock = new Stopwatch();
        int files = 0, hits = 0, details = 0;
        int? minimum = null, maximum = null;
        double? first = null;
        var results = new Inline<FileResult>(result =>
        {
            first ??= clock.Elapsed.TotalMilliseconds;
            files++; hits += result.TotalOccurrences;
            minimum = Math.Min(minimum ?? int.MaxValue, result.TotalOccurrences);
            maximum = Math.Max(maximum ?? 0, result.TotalOccurrences);
            digest?.Add(result);
        });
        var issues = new Inline<SearchIssue>(_ => details++);
        var engine = new TextSearchEngine(DocumentExtractorCatalog.CreateDefault());
        long heap = GC.GetTotalMemory(false), allocated = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        TimeSpan cpu = process.TotalProcessorTime;
        clock.Start();
        int count = expected.Scenario switch { "single-long" => 1, "regex" => 16,
            _ => int.Parse(expected.Scenario[(expected.Scenario.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture) };
        var request = new SearchRequest(directory, expected.Scenario == "regex" ? [] :
            Enumerable.Range(0, count).Select(index => expected.Scenario == "unicode-none-64" ? $"缺失{index:D3}终" :
                expected.CorpusScenario == "unicode" ? UnicodeTerm(index) : Term(index)).ToArray(), SearchOperator.Or, ContextCharacters: 0)
        { MatchMode = expected.Scenario == "regex" ? SearchMatchMode.RegularExpression : SearchMatchMode.Literal,
            RegexPattern = expected.Scenario == "regex" ? "zzneedle[0-9]{3}zz" : null };
        var summary = await engine.SearchAsync(request, results: results, issues: issues, maxConcurrency: 1).ConfigureAwait(false);
        clock.Stop();
        long delta = GC.GetTotalAllocatedBytes(true) - allocated;
        var gc = GC.GetGCMemoryInfo();
        process.Refresh();
        var memory = new MemoryMeasurement(process.PeakWorkingSet64, process.WorkingSet64, heap, GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.FragmentedBytes, delta,
            new GcCounts(GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2));
        var errors = new List<string>();
        if (summary.Status.ToString() != expected.Status) errors.Add("status");
        if (summary.Progress.ScannedFiles != 1 || summary.Progress.MatchedFiles != expected.MatchedFiles ||
            summary.Progress.Occurrences != expected.Occurrences || files != expected.MatchedFiles || hits != expected.Occurrences)
            errors.Add("counts");
        if (summary.Diagnostics.ErrorCount != 0 || summary.Diagnostics.WarningCount != 0 || details != 0) errors.Add("issues");
        return new RoundMeasurement(number, verify ? "verification-excluded" : number == 1 ? "first-round" : "repeat",
            summary.Status.ToString(), clock.Elapsed.TotalMilliseconds, first, summary.Progress.ScannedFiles,
            summary.Progress.MatchedFiles, summary.Progress.Occurrences, files, hits, minimum, maximum,
            summary.Diagnostics.ErrorCount, summary.Diagnostics.WarningCount, summary.Diagnostics.SuppressedIssueCount,
            details, null, memory, null, errors.Count == 0, errors.AsReadOnly(), digest?.OrderSha256(),
            digest?.ContentSha256(), (process.TotalProcessorTime - cpu).TotalMilliseconds);
    }
    private static string Stamp(string root, LiteralManifest manifest) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { manifest, marker = Hash(Path.Combine(root, Marker)),
            controls = new[] { Marker, Manifest }.Select(name => new FileInfo(Path.Combine(root, name)).LastWriteTimeUtc.Ticks).ToArray(),
            manifestHash = Hash(Path.Combine(root, Manifest)) })));
    private static string Root(string root)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (path == Path.GetPathRoot(path)) throw new BenchmarkException("filesystem-root-not-allowed");
        BenchmarkCorpus.AssertOrdinaryAncestors(path);
        return path;
    }
    private static void WriteNew(string path, string text)
    {
        BenchmarkCorpus.AssertOrdinaryAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
    private static string Hash(string path)
    {
        Ordinary(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Ordinary(string path)
    {
        BenchmarkCorpus.AssertOrdinaryAncestors(path);
        if (!OperatingSystem.IsWindows()) return;
        using var handle = File.OpenHandle(path);
        if (!GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()) || info.Links != 1)
            throw new BenchmarkException("literal-linked-or-unreadable-file");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long Allocation, End; public uint Links; public byte DeletePending, Directory; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
