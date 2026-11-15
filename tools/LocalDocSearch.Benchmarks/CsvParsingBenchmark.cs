using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalDocSearch.Core;
using Microsoft.Win32.SafeHandles;

namespace LocalDocSearch.Benchmarks;

public sealed record CsvCorpusFile(string RelativePath, string Kind, long Bytes, long RecordCount,
    long FieldCount, long TextCharacters, int MaxFieldCharacters, long LastWriteTimeUtcTicks, string Sha256);
public sealed record CsvManifest(string Generator, bool Quick, IReadOnlyList<CsvCorpusFile> Files);
public sealed record CsvExpectation(string Scenario, string Kind, string Mode, long Bytes, long RecordCount,
    long FieldCount, long TextCharacters, int MaxFieldCharacters, int EngineOccurrences);
public sealed record CsvCounts(string Status, long SegmentCount, long RecordCount, long TextCharacters,
    int ScannedFiles, int PublishedFiles, int Occurrences, long Warnings, long Errors, int IssueDetails);
public sealed record CsvTimedRound(int Round, string Phase, double ElapsedMilliseconds,
    double? FirstResultMilliseconds, double CpuMilliseconds, MemoryMeasurement Memory, CsvCounts Counts,
    bool Passed, IReadOnlyList<string> Mismatches);
public sealed record CsvVerification(int Round, string Phase, CsvCounts DirectCounts, CsvCounts EngineCounts,
    string SegmentSha256, string ExpectedSegmentSha256, string ExtractionWarningsSha256,
    string EngineResultSha256, string ExpectedEngineResultSha256, string EngineIssuesSha256,
    bool Passed, IReadOnlyList<string> Mismatches);
public sealed record CsvReport(int SchemaVersion, string Tool, string Scenario, int Workers, MachineInfo Machine,
    CsvExpectation Expected, IReadOnlyList<CsvTimedRound> Rounds, IReadOnlyList<CsvVerification> Verifications,
    string CoreSha256, string DriverSha256, string SourceSha256Before, string SourceSha256After, bool Passed,
    string TimingSemantics = "direct: extractor construction, open/read/decode/parse and complete enumeration to EOF; engine: construction/request/query/read/decode/parse/search to completion; only counts/first result in timed work; every round has a separate after-round direct+engine verification; all SHA and integrity checks excluded; OS cache not cleared",
    string PeakSemantics = "process lifetime cumulative peak, including preceding rounds and their verification; not a per-round peak; allocation delta is process-wide GC.GetTotalAllocatedBytes(precise:true); no forced collection",
    string ComparisonSemantics = "use byte-identical driver/dependencies/corpus in old/new ABBA processes; replace only LocalDocSearch.Core.dll; compare counts and all verification digests before performance",
    string DigestSemantics = "SHA256 of length-prefixed UTF8 JSON frames in enumeration/publication order; TextSegment includes every ID/Text/Source property; warnings and issues have separate ordered digests; FileResult includes all serialized fields with only FullPath replaced by scenario-relative path",
    IReadOnlyList<string>? OmittedFields = null);

/// <summary>Original, bounded CSV fixtures and a common driver for Core-only old/new comparisons.</summary>
public static class CsvParsingBenchmark
{
    public const string Marker = ".csv-parsing-root", Manifest = "csv-manifest.json";
    public const string Generator = "LocalDocSearch/csv-parsing-v1";
    private const string Needle = "csvbenchneedle", Missing = "csvbenchabsent";
    private const long StandardBytes = 8L * 1024 * 1024;
    private static readonly string[] Kinds = ["plain", "quoted", "boundary"];
    public static IReadOnlyList<string> Scenarios { get; } = Array.AsReadOnly(new[]
    {
        "direct-plain", "direct-quoted", "direct-boundary",
        "engine-sparse-plain", "engine-sparse-quoted", "engine-sparse-boundary",
        "engine-none-plain", "engine-none-quoted", "engine-none-boundary"
    });

    public static CsvManifest Prepare(string root, bool quick = false)
    {
        root = Root(root);
        if (File.Exists(root) || Directory.Exists(root)) throw new BenchmarkException("csv-root-must-be-new");
        Directory.CreateDirectory(root);
        WriteNew(Path.Combine(root, Marker), Generator);
        var files = new List<CsvCorpusFile>();
        foreach (string kind in Kinds)
        {
            string directory = Path.Combine(root, kind);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "original.csv");
            BenchmarkCorpus.AssertOrdinaryAncestors(path);
            CsvCorpusFile generated;
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                generated = Generate(kind, quick, stream);
            files.Add(generated with { LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks });
        }
        var manifest = new CsvManifest(Generator, quick, files.AsReadOnly());
        WriteNew(Path.Combine(root, Manifest), BenchmarkJson.Serialize(manifest));
        OpenVerified(root);
        return manifest;
    }

    public static CsvManifest OpenVerified(string root)
    {
        root = Root(root);
        string manifestPath = Path.Combine(root, Manifest), markerPath = Path.Combine(root, Marker);
        Ordinary(manifestPath);
        Ordinary(markerPath);
        if (new FileInfo(manifestPath).Length > 16384 || new FileInfo(markerPath).Length > 128 ||
            File.ReadAllText(markerPath) != Generator) throw new BenchmarkException("csv-invalid-marker");
        CsvManifest manifest;
        try { manifest = JsonSerializer.Deserialize<CsvManifest>(File.ReadAllText(manifestPath), BenchmarkJson.Options)!; }
        catch (JsonException) { throw new BenchmarkException("csv-invalid-manifest"); }
        if (manifest is null || manifest.Generator != Generator || manifest.Files is null || manifest.Files.Count != Kinds.Length)
            throw new BenchmarkException("csv-invalid-manifest");
        var entries = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!entries.SetEquals(Kinds.Concat([Marker, Manifest]))) throw new BenchmarkException("csv-unowned-entry");
        foreach (string kind in Kinds)
        {
            string directory = Path.Combine(root, kind);
            BenchmarkCorpus.AssertOrdinaryAncestors(directory);
            var selected = manifest.Files.Where(file => file is not null && file.Kind == kind).ToArray();
            if (selected.Length != 1) throw new BenchmarkException("csv-invalid-manifest");
            var file = selected[0];
            if (file.RelativePath != kind + "/original.csv") throw new BenchmarkException("invalid-manifest-path");
            string path = BenchmarkCorpus.ResolveOwnedPath(root, file.RelativePath);
            Ordinary(path);
            if (!Directory.GetFileSystemEntries(directory).SequenceEqual([path])) throw new BenchmarkException("csv-unowned-entry");
            var original = Generate(kind, manifest.Quick);
            var info = new FileInfo(path);
            if (file != (original with { LastWriteTimeUtcTicks = file.LastWriteTimeUtcTicks }) ||
                info.Length != file.Bytes || info.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtcTicks || Hash(path) != file.Sha256)
                throw new BenchmarkException("csv-integrity-mismatch");
        }
        return manifest;
    }

    public static CsvExpectation Expected(string scenario, bool quick)
    {
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal)) throw new BenchmarkException("unknown-scenario");
        string kind = scenario[(scenario.LastIndexOf('-') + 1)..];
        string mode = scenario.StartsWith("direct-", StringComparison.Ordinal) ? "direct" : "engine";
        var file = Generate(kind, quick);
        return new(scenario, kind, mode, file.Bytes, file.RecordCount, file.FieldCount, file.TextCharacters,
            file.MaxFieldCharacters, scenario.StartsWith("engine-sparse-", StringComparison.Ordinal) ? 1 : 0);
    }

    public static async Task<CsvReport> RunAsync(string root, string scenario, int repeat = 3)
    {
        if (repeat is < 1 or > 20) throw new BenchmarkException("invalid-repeat");
        Expected(scenario, true); // Reject argument errors before touching the corpus.
        root = Root(root);
        var manifest = OpenVerified(root);
        string before = Stamp(root, manifest);
        var expected = Expected(scenario, manifest.Quick);
        var file = manifest.Files.Single(item => item.Kind == expected.Kind);
        string directory = Path.Combine(root, file.Kind);
        var oracle = Oracle(file, expected.EngineOccurrences);
        var rounds = new List<CsvTimedRound>();
        var verifications = new List<CsvVerification>();
        for (int number = 1; number <= repeat; number++)
        {
            rounds.Add(await Measure(directory, file, expected, number).ConfigureAwait(false));
            verifications.Add(await Verify(directory, file, expected, number, oracle).ConfigureAwait(false));
        }
        var afterManifest = OpenVerified(root);
        string after = Stamp(root, afterManifest);
        if (before != after) throw new BenchmarkException("csv-control-integrity-mismatch");
        var machine = new MachineInfo(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Stopwatch.Frequency,
            System.Runtime.GCSettings.IsServerGC);
        return new(1, Generator, scenario, 1, machine, expected, rounds.AsReadOnly(), verifications.AsReadOnly(),
            Hash(typeof(TextSearchEngine).Assembly.Location), Hash(typeof(CsvParsingBenchmark).Assembly.Location),
            before, after, rounds.All(round => round.Passed) && verifications.All(round => round.Passed),
            OmittedFields: BenchmarkRunner.OmittedFields);
    }

    private static async Task<CsvTimedRound> Measure(string directory, CsvCorpusFile file, CsvExpectation expected, int number)
    {
        using var process = Process.GetCurrentProcess();
        var clock = new Stopwatch();
        double? first = null;
        void First() => first ??= clock.Elapsed.TotalMilliseconds;
        long heap = GC.GetTotalMemory(false), allocated = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        TimeSpan cpu = process.TotalProcessorTime;
        clock.Start();
        var counts = expected.Mode == "direct"
            ? await Extract(directory, First).ConfigureAwait(false)
            : await Search(directory, expected.EngineOccurrences, First).ConfigureAwait(false);
        clock.Stop();
        // Snapshot timing/allocation before constructing verification objects or hashing anything.
        double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        long delta = GC.GetTotalAllocatedBytes(true) - allocated;
        var gc = GC.GetGCMemoryInfo();
        process.Refresh();
        var memory = new MemoryMeasurement(process.PeakWorkingSet64, process.WorkingSet64, heap, GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.FragmentedBytes, delta,
            new GcCounts(GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2));
        var errors = CheckCounts(counts, file, expected.Mode == "direct", expected.EngineOccurrences);
        return new(number, number == 1 ? "first-round" : "repeat", clock.Elapsed.TotalMilliseconds,
            first, cpuMilliseconds, memory, counts, errors.Count == 0, errors.AsReadOnly());
    }

    private static async Task<CsvVerification> Verify(string directory, CsvCorpusFile file,
        CsvExpectation expected, int number, OracleResult oracle)
    {
        using var segments = new OrderedDigest();
        using var warnings = new OrderedDigest();
        using var results = new OrderedDigest();
        using var issues = new OrderedDigest();
        var direct = await Extract(directory, static () => { }, segments, warnings).ConfigureAwait(false);
        var engine = await Search(directory, expected.EngineOccurrences, static () => { }, results, issues).ConfigureAwait(false);
        string segmentSha = segments.Finish(), warningSha = warnings.Finish(), resultSha = results.Finish(), issueSha = issues.Finish();
        var errors = CheckCounts(direct, file, true, 0).Select(error => "direct-" + error).ToList();
        errors.AddRange(CheckCounts(engine, file, false, expected.EngineOccurrences).Select(error => "engine-" + error));
        if (segmentSha != oracle.Segments) errors.Add("segments-digest");
        if (warningSha != EmptyDigest) errors.Add("warnings-digest");
        if (resultSha != oracle.Engine) errors.Add("engine-result-digest");
        if (issueSha != EmptyDigest) errors.Add("engine-issues-digest");
        return new(number, "after-round-excluded", direct, engine, segmentSha, oracle.Segments, warningSha,
            resultSha, oracle.Engine, issueSha, errors.Count == 0, errors.AsReadOnly());
    }

    private static async Task<CsvCounts> Extract(string directory, Action first,
        OrderedDigest? segments = null, OrderedDigest? warnings = null)
    {
        long fields = 0, records = 0, characters = 0, warningCount = 0;
        var context = new ExtractionContext(warning => { warningCount++; warnings?.Add(warning); });
        var extractor = new CsvTextExtractor();
        await foreach (var segment in extractor.ExtractAsync(Path.Combine(directory, "original.csv"), context).ConfigureAwait(false))
        {
            if (fields == 0) first();
            fields++;
            records = Math.Max(records, segment.Source.Record ?? 0);
            characters += segment.Text.Length;
            segments?.Add(segment);
        }
        return new("Completed", fields, records, characters, 1, 0, 0, warningCount, 0, 0);
    }

    private static async Task<CsvCounts> Search(string directory, int occurrences, Action first,
        OrderedDigest? results = null, OrderedDigest? issues = null)
    {
        int files = 0, hits = 0, details = 0;
        var engine = new TextSearchEngine([new CsvTextExtractor()]);
        var request = new SearchRequest(directory, [occurrences == 1 ? Needle : Missing], SearchOperator.Or,
            Extensions: [".csv"], MatchCase: true, ContextCharacters: 0);
        var summary = await engine.SearchAsync(request, results: new Inline<FileResult>(result =>
        {
            if (files == 0) first();
            files++;
            hits += result.TotalOccurrences;
            if (results is not null) results.Add(result with { FullPath = Relative(directory, result.FullPath) });
        }), issues: new Inline<SearchIssue>(issue =>
        {
            details++;
            if (issues is not null) issues.Add(issue with { Path = Relative(directory, issue.Path) });
        }), maxConcurrency: 1).ConfigureAwait(false);
        // Cross-check engine counters and published counters without retaining result bodies.
        bool countersAgree = files == summary.Progress.MatchedFiles && hits == summary.Progress.Occurrences &&
            summary.Progress.SkippedFiles == 0 && summary.Diagnostics.SuppressedIssueCount == 0 && summary.Diagnostics.LimitReason is null;
        return new(countersAgree ? summary.Status.ToString() : "CounterMismatch", 0, 0, 0,
            summary.Progress.ScannedFiles, files, hits, summary.Diagnostics.WarningCount, summary.Diagnostics.ErrorCount, details);
    }

    private static List<string> CheckCounts(CsvCounts counts, CsvCorpusFile file, bool direct, int occurrences)
    {
        var errors = new List<string>();
        if (counts.Status != "Completed") errors.Add("status");
        if (counts.ScannedFiles != 1 || counts.PublishedFiles != (direct || occurrences == 0 ? 0 : 1) ||
            counts.Occurrences != (direct ? 0 : occurrences)) errors.Add("files-or-hits");
        if (counts.SegmentCount != (direct ? file.FieldCount : 0) || counts.RecordCount != (direct ? file.RecordCount : 0) ||
            counts.TextCharacters != (direct ? file.TextCharacters : 0)) errors.Add("extraction-counts");
        if (counts.Warnings != 0 || counts.Errors != 0 || counts.IssueDetails != 0) errors.Add("issues");
        return errors;
    }

    private sealed record RowData(string Encoded, string[] Fields, long[] LineOffsets, int PhysicalNewlines);
    private sealed record OracleResult(string Segments, string Engine);

    private static RowData Row(string kind, bool last)
    {
        int size = kind == "plain" ? 2048 : kind == "quoted" ? 4096 : 32768;
        string[] fields = kind switch
        {
            "plain" => ["original orchard", new string('p', 257), "plain parcel", ""],
            "quoted" => ["orchard,blue", "a \"quoted\" note", "line-one\r\nline-two\rtail\nend", ""],
            "boundary" => [new string('b', 8190) + "\"" + new string('c', 8190) + "\r\nfold\rend", "edge,\"parcel\"", ""],
            _ => throw new BenchmarkException("unknown-corpus-kind")
        };
        string ending = kind == "plain" ? "\n" : kind == "boundary" && last ? "" : "\r\n";
        bool quoted = kind != "plain";
        string Encode(string value) => quoted ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
        int padding = size - (string.Join(',', fields.Select(Encode)) + ending).Length;
        string suffix = last ? " " + Needle : "";
        fields[^1] = new string('x', padding - suffix.Length) + suffix;
        string encoded = string.Join(',', fields.Select(Encode)) + ending;
        var offsets = new long[fields.Length];
        long line = 0;
        for (int index = 0; index < fields.Length; index++)
        {
            offsets[index] = line;
            fields[index] = Normalize(fields[index]);
            line += fields[index].Count(character => character == '\n');
        }
        // ASCII intentionally fixes byte/character offsets: escaped quotes straddle 8191/8192,
        // and a CRLF straddles 16383/16384 in every boundary record. The final record ends at EOF.
        return new(encoded, fields, offsets, checked((int)line + (ending.Length == 0 ? 0 : 1)));
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static long FileBytes(string kind, bool quick) => quick ? kind == "boundary" ? 128 * 1024 : 64 * 1024 : StandardBytes;

    private static CsvCorpusFile Generate(string kind, bool quick, Stream? destination = null)
    {
        RowData ordinary = Row(kind, false), last = Row(kind, true);
        byte[] bytes = Encoding.UTF8.GetBytes(ordinary.Encoded), lastBytes = Encoding.UTF8.GetBytes(last.Encoded);
        long records = FileBytes(kind, quick) / bytes.Length;
        if (records < 2 || lastBytes.Length != bytes.Length) throw new BenchmarkException("csv-generator-size");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long record = 0; record < records; record++)
        {
            byte[] current = record == records - 1 ? lastBytes : bytes;
            destination?.Write(current);
            hash.AppendData(current);
        }
        long ordinaryCharacters = ordinary.Fields.Sum(field => (long)field.Length);
        long characters = (records - 1) * ordinaryCharacters + last.Fields.Sum(field => (long)field.Length);
        int maximum = ordinary.Fields.Concat(last.Fields).Max(field => field.Length);
        return new(kind + "/original.csv", kind, records * bytes.Length, records, records * ordinary.Fields.Length,
            characters, maximum, 0, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static OracleResult Oracle(CsvCorpusFile file, int occurrences)
    {
        using var segments = new OrderedDigest();
        using var engine = new OrderedDigest();
        RowData ordinary = Row(file.Kind, false), last = Row(file.Kind, true);
        long line = 1;
        TextSegment? final = null;
        for (long record = 1; record <= file.RecordCount; record++)
        {
            var row = record == file.RecordCount ? last : ordinary;
            for (int index = 0; index < row.Fields.Length; index++)
            {
                final = new TextSegment($"csv:{record}:{index + 1}", row.Fields[index],
                    new SourceLocator(SourceKind.CsvField, Line: line + row.LineOffsets[index], Record: record, Field: index + 1));
                segments.Add(final);
            }
            line += row.PhysicalNewlines;
        }
        if (occurrences == 1)
        {
            var segment = final!;
            var match = new MatchItem(Needle, "", Needle, "", segment.Source, segment.Id, segment.Text.Length - Needle.Length);
            engine.Add(new FileResult("original.csv", ".csv", new DateTime(file.LastWriteTimeUtcTicks, DateTimeKind.Utc), [match]));
        }
        return new(segments.Finish(), engine.Finish());
    }

    private static readonly string EmptyDigest = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
    private sealed class OrderedDigest : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Add<T>(T value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            _hash.AppendData(length);
            _hash.AppendData(bytes);
        }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        public void Dispose() => _hash.Dispose();
    }

    private static string Relative(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new BenchmarkException("csv-result-outside-scenario-root");
        return relative;
    }
    private static string Stamp(string root, CsvManifest manifest) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { manifest, marker = Hash(Path.Combine(root, Marker)),
            manifestHash = Hash(Path.Combine(root, Manifest)), controls = new[] { Marker, Manifest }
                .Select(name => new FileInfo(Path.Combine(root, name)).LastWriteTimeUtc.Ticks).ToArray() })));
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
            throw new BenchmarkException("csv-linked-or-unreadable-file");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long Allocation, End; public uint Links; public byte DeletePending, Directory; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
