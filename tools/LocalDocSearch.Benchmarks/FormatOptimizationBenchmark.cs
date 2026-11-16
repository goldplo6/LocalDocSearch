using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using Microsoft.Win32.SafeHandles;

namespace LocalDocSearch.Benchmarks;

public sealed record FormatCorpusFile(string Kind, string RelativePath, long Bytes, long Segments, long TextCharacters,
    int MaxSegmentCharacters, int WarningCount, long LastWriteTimeUtcTicks, string Sha256);
public sealed record FormatManifest(string Generator, bool Quick, IReadOnlyList<FormatCorpusFile> Files);
public sealed record FormatExpectation(string Scenario, string Kind, string Mode, string Encoding, long Segments,
    long TextCharacters, int MaxSegmentCharacters, int EngineOccurrences, int WarningCount);
public sealed record FormatCounts(string Status, long Segments, long TextCharacters, int ScannedFiles, int PublishedFiles,
    int Occurrences, long Warnings, long Errors, int IssueDetails, int SkippedFiles, long SuppressedIssues, string? LimitReason);
public sealed record FormatSourceStamp(long Bytes, long LastWriteTimeUtcTicks, string Sha256);
public sealed record FormatTimedRound(int Round, string Phase, double ElapsedMilliseconds, double? FirstResultMilliseconds,
    double CpuMilliseconds, MemoryMeasurement Memory, FormatCounts Counts, bool Passed, IReadOnlyList<string> Mismatches);
public sealed record FormatVerification(int Round, string Phase, FormatSourceStamp SourceBefore, FormatSourceStamp SourceAfter,
    FormatCounts DirectCounts, FormatCounts EngineCounts, string SegmentSha256, string ExpectedSegmentSha256,
    string ExtractionWarningsSha256, string ExpectedExtractionWarningsSha256, string EngineResultSha256,
    string ExpectedEngineResultSha256, string EngineIssuesSha256, string ExpectedEngineIssuesSha256,
    string EngineSummarySha256, string ExpectedEngineSummarySha256, bool Passed, IReadOnlyList<string> Mismatches);
public sealed record FormatRetainedPoint(long CurrentSegment, long TotalSegments, long ManagedLiveBytes);
public sealed record FormatRetainedObservation(string Phase, long EnumeratedSegments, long TextCharacters,
    IReadOnlyList<FormatRetainedPoint> Points, bool Passed, string Semantics =
    "Separate complete direct enumeration outside all timers, retaining only enumerator.Current; full GC at first, half and three-quarter consumption; managed live consumption retention, NOT extraction peak; no forced GC in timed rounds");
public sealed record FormatReport(int SchemaVersion, string Tool, string Scenario, int Workers, MachineInfo Machine,
    FormatExpectation Expected, IReadOnlyList<FormatTimedRound> Rounds, IReadOnlyList<FormatVerification> Verifications,
    string LoadedCoreSha256, string LoadedExtractorsSha256, string DriverSha256, string SourceSha256Before,
    string SourceSha256After, FormatRetainedObservation? RetainedObservation, bool Passed,
    string TimingSemantics = "direct includes construction/open/read/decode/parse and complete enumeration to EOF; engine includes construction/request/query/read/decode/parse/matching to completion; timed callbacks only count and capture first result; every timed round followed by separate full direct+engine verification; source integrity, all SHA, oracle generation, retained observations and report serialization excluded; no forced GC in timing; OS cache not cleared",
    string PeakSemantics = "process lifetime cumulative peak including preceding rounds and verification; NOT per-round peak or extraction-only peak; allocation delta is process-wide GC.GetTotalAllocatedBytes(precise:true); no forced collection in timing",
    string DigestSemantics = "ordered length-prefixed UTF8 JSON frames; every TextSegment ID/Text/Source field, every warning, complete FileResult matches/context/source/warnings, issues and SearchSummary status/progress/diagnostics; FullPath/Path/CurrentPath normalized to fixture filename; ONLY nondeterministic SearchDiagnostics.ElapsedMilliseconds normalized to zero and separately measured by elapsed timing; no content or terms published",
    string ComparisonSemantics = "byte-identical shared driver and dependency inventory; only LocalDocSearch.Core.dll and LocalDocSearch.Extractors.dll may differ; ABBA four groups/repeat three gives six samples per version; compare all counts/digests before interpreting performance");

/// <summary>Original fixtures, independent oracle and common byte-identical driver for format experiments.</summary>
public static class FormatOptimizationBenchmark
{
    public const string Generator = "LocalDocSearch/format-optimization-v1", Marker = ".format-optimization-root", Manifest = "format-manifest.json";
    private const string Needle = "formatbenchneedle", Missing = "formatbenchabsent";
    private const int Context = 12;
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly string[] Kinds = ["txt-small", "txt-large", "md-small", "md-large", "log-small", "log-large",
        "csv-plain", "csv-quoted", "csv-boundary", "xlsx-shared", "xlsx-inline", "docx-paragraphs", "docx-tables", "pptx", "pdf"];
    public static IReadOnlyList<string> Scenarios { get; } = Array.AsReadOnly(
        Kinds.SelectMany(kind => new[] { "engine-none-" + kind, "engine-sparse-" + kind })
            .Concat(Kinds.Where(kind => !IsText(kind)).Select(kind => "direct-" + kind))
            .Concat(new[] { "txt", "md", "log" }.SelectMany(kind => new[] {
                "engine-none-" + kind + "-large-autoUTF8", "engine-sparse-" + kind + "-large-autoUTF8" })).ToArray());

    public static FormatManifest Prepare(string root, bool quick = false)
    {
        root = Root(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new BenchmarkException("format-root-must-be-new");
        Directory.CreateDirectory(root);
        WriteNew(Path.Combine(root, Marker), Generator);
        var files = new List<FormatCorpusFile>();
        foreach (string kind in Kinds)
        {
            string directory = Path.Combine(root, kind);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Filename(kind));
            OrdinaryAncestors(path);
            FormatCorpusFile file;
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file = Generate(kind, quick, stream);
            files.Add(file with { LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks });
        }
        var manifest = new FormatManifest(Generator, quick, files.AsReadOnly());
        WriteNew(Path.Combine(root, Manifest), BenchmarkJson.Serialize(manifest));
        OpenVerified(root);
        return manifest;
    }

    public static FormatManifest OpenVerified(string root)
    {
        root = Root(root);
        string marker = Path.Combine(root, Marker), metadata = Path.Combine(root, Manifest);
        Ordinary(marker); Ordinary(metadata);
        if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != Generator || new FileInfo(metadata).Length > 32768)
            throw new BenchmarkException("format-invalid-marker");
        FormatManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<FormatManifest>(File.ReadAllText(metadata), BenchmarkJson.Options); }
        catch (JsonException) { throw new BenchmarkException("format-invalid-manifest"); }
        if (manifest is null || manifest.Generator != Generator || manifest.Files is null || manifest.Files.Count != Kinds.Length)
            throw new BenchmarkException("format-invalid-manifest");
        if (!Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal)
            .SetEquals(Kinds.Concat([Marker, Manifest]))) throw new BenchmarkException("format-unowned-entry");
        foreach (string kind in Kinds)
        {
            var selected = manifest.Files.Where(file => file is not null && file.Kind == kind).ToArray();
            if (selected.Length != 1) throw new BenchmarkException("format-invalid-manifest");
            var file = selected[0];
            if (file.RelativePath != kind + "/" + Filename(kind)) throw new BenchmarkException("invalid-manifest-path");
            string path = BenchmarkCorpus.ResolveOwnedPath(root, file.RelativePath);
            Ordinary(path);
            if (!Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).SequenceEqual([path]))
                throw new BenchmarkException("format-unowned-entry");
            // Recreate original bytes into a hashing sink, independent of loaded extractor assemblies.
            var original = Generate(kind, manifest.Quick);
            if (file != (original with { LastWriteTimeUtcTicks = file.LastWriteTimeUtcTicks }) ||
                Source(path) != new FormatSourceStamp(file.Bytes, file.LastWriteTimeUtcTicks, file.Sha256))
                throw new BenchmarkException("format-integrity-mismatch");
        }
        return manifest;
    }

    public static FormatExpectation Expected(string scenario, bool quick)
    {
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal)) throw new BenchmarkException("unknown-scenario");
        bool direct = scenario.StartsWith("direct-", StringComparison.Ordinal);
        string kind = scenario[(direct ? 7 : scenario.StartsWith("engine-none-", StringComparison.Ordinal) ? 12 : 14)..];
        bool auto = kind.EndsWith("-autoUTF8", StringComparison.Ordinal);
        if (auto) kind = kind[..^9];
        var stats = Statistics(kind, quick);
        return new(scenario, kind, direct ? "direct" : "engine", auto ? "Utf8OrGb18030" : "StrictUnicode",
            stats.Segments, stats.Characters, stats.Maximum, scenario.StartsWith("engine-sparse-", StringComparison.Ordinal) ? 1 : 0,
            Warning(kind) is null ? 0 : 1);
    }

    public static async Task<FormatReport> RunAsync(string root, string scenario, int repeat = 3, bool observeRetained = false)
    {
        if (repeat is < 1 or > 20) throw new BenchmarkException("invalid-repeat");
        Expected(scenario, true);
        root = Root(root);
        var manifest = OpenVerified(root);
        string before = Stamp(root, manifest);
        var expected = Expected(scenario, manifest.Quick);
        if (observeRetained && (expected.Mode != "direct" || expected.Kind.StartsWith("csv-", StringComparison.Ordinal)))
            throw new BenchmarkException("retained-requires-office-or-pdf-direct");
        var file = manifest.Files.Single(item => item.Kind == expected.Kind);
        string path = BenchmarkCorpus.ResolveOwnedPath(root, file.RelativePath);
        var oracle = Oracle(file, expected, manifest.Quick);
        var rounds = new List<FormatTimedRound>();
        var verifications = new List<FormatVerification>();
        for (int round = 1; round <= repeat; round++)
        {
            var sourceBefore = Source(path); AssertSource(file, sourceBefore);
            rounds.Add(await Measure(path, expected, round).ConfigureAwait(false));
            var verification = await Verify(path, file, expected, round, sourceBefore, oracle).ConfigureAwait(false);
            verifications.Add(verification);
        }
        FormatRetainedObservation? retained = observeRetained
            ? await ObserveRetained(path, expected).ConfigureAwait(false) : null;
        string after = Stamp(root, OpenVerified(root));
        if (before != after) throw new BenchmarkException("format-control-integrity-mismatch");
        var machine = new MachineInfo(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Stopwatch.Frequency,
            System.Runtime.GCSettings.IsServerGC);
        return new(1, Generator, scenario, 1, machine, expected, rounds.AsReadOnly(), verifications.AsReadOnly(),
            Hash(typeof(TextSearchEngine).Assembly.Location), Hash(typeof(WordTextExtractor).Assembly.Location),
            Hash(typeof(FormatOptimizationBenchmark).Assembly.Location), before, after, retained,
            rounds.All(item => item.Passed) && verifications.All(item => item.Passed) && (retained?.Passed ?? true));
    }

    private static async Task<FormatTimedRound> Measure(string path, FormatExpectation expected, int round)
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
            ? await Direct(path, expected, First).ConfigureAwait(false)
            : await Engine(path, expected, First).ConfigureAwait(false);
        clock.Stop();
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        long delta = GC.GetTotalAllocatedBytes(true) - allocated;
        var gc = GC.GetGCMemoryInfo();
        process.Refresh();
        var memory = new MemoryMeasurement(process.PeakWorkingSet64, process.WorkingSet64, heap, GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.FragmentedBytes, delta,
            new GcCounts(GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2));
        var errors = Check(counts, expected, expected.Mode == "direct");
        return new(round, round == 1 ? "first-round" : "repeat", clock.Elapsed.TotalMilliseconds, first, cpuMs,
            memory, counts, errors.Count == 0, errors.AsReadOnly());
    }

    private sealed record OracleDigests(string Segments, string Warnings, string Results, string Issues, string Summary);
    private static async Task<FormatVerification> Verify(string path, FormatCorpusFile file, FormatExpectation expected,
        int round, FormatSourceStamp before, OracleDigests oracle)
    {
        using var segments = new OrderedDigest(); using var warnings = new OrderedDigest();
        using var results = new OrderedDigest(); using var issues = new OrderedDigest(); using var summary = new OrderedDigest();
        var direct = await Direct(path, expected, static () => { }, segments, warnings).ConfigureAwait(false);
        var engine = await Engine(path, expected, static () => { }, results, issues, summary).ConfigureAwait(false);
        string seg = segments.Finish(), warn = warnings.Finish(), res = results.Finish(), iss = issues.Finish(), sum = summary.Finish();
        var errors = Check(direct, expected, true).Select(error => "direct-" + error).ToList();
        errors.AddRange(Check(engine, expected, false).Select(error => "engine-" + error));
        if (seg != oracle.Segments) errors.Add("segments-digest");
        if (warn != oracle.Warnings) errors.Add("warnings-digest");
        if (res != oracle.Results) errors.Add("engine-results-digest");
        if (iss != oracle.Issues) errors.Add("engine-issues-digest");
        if (sum != oracle.Summary) errors.Add("engine-summary-digest");
        var after = Source(path); AssertSource(file, after);
        if (before != after) errors.Add("source-integrity");
        return new(round, "after-round-excluded", before, after, direct, engine, seg, oracle.Segments, warn, oracle.Warnings,
            res, oracle.Results, iss, oracle.Issues, sum, oracle.Summary, errors.Count == 0, errors.AsReadOnly());
    }

    private static async Task<FormatCounts> Direct(string path, FormatExpectation expected, Action first,
        OrderedDigest? segments = null, OrderedDigest? warnings = null)
    {
        long count = 0, characters = 0, warningCount = 0;
        var context = new ExtractionContext(warning => { warningCount++; warnings?.Add(warning); }, EncodingMode(expected));
        await foreach (var segment in Extractor(expected.Kind).ExtractAsync(path, context).ConfigureAwait(false))
        {
            if (count == 0) first();
            count++; characters += segment.Text.Length; segments?.Add(segment);
        }
        return new("Completed", count, characters, 1, 0, 0, warningCount, 0, 0, 0, 0, null);
    }

    private static async Task<FormatCounts> Engine(string path, FormatExpectation expected, Action first,
        OrderedDigest? results = null, OrderedDigest? issues = null, OrderedDigest? summaries = null)
    {
        int files = 0, hits = 0, details = 0;
        string directory = Path.GetDirectoryName(path)!;
        var engine = new TextSearchEngine([Extractor(expected.Kind)]);
        var request = new SearchRequest(directory, [expected.EngineOccurrences == 1 ? Needle : Missing], SearchOperator.Or,
            Extensions: [Path.GetExtension(path)], MatchCase: true, ContextCharacters: Context, EncodingMode: EncodingMode(expected));
        var summary = await engine.SearchAsync(request, results: new Inline<FileResult>(result =>
        {
            if (files == 0) first();
            files++; hits += result.TotalOccurrences;
            if (results is not null) results.Add(result with { FullPath = Relative(directory, result.FullPath) });
        }), issues: new Inline<SearchIssue>(issue =>
        {
            details++;
            if (issues is not null) issues.Add(issue with { Path = Relative(directory, issue.Path) });
        }), maxConcurrency: 1).ConfigureAwait(false);
        if (summaries is not null) summaries.Add(summary with
        {
            Progress = summary.Progress with { CurrentPath = summary.Progress.CurrentPath is null ? null : Relative(directory, summary.Progress.CurrentPath) },
            Diagnostics = summary.Diagnostics with { ElapsedMilliseconds = 0 }
        });
        bool agree = files == summary.Progress.MatchedFiles && hits == summary.Progress.Occurrences;
        return new(agree ? summary.Status.ToString() : "CounterMismatch", 0, 0, summary.Progress.ScannedFiles, files, hits,
            summary.Diagnostics.WarningCount, summary.Diagnostics.ErrorCount, details, summary.Progress.SkippedFiles,
            summary.Diagnostics.SuppressedIssueCount, summary.Diagnostics.LimitReason);
    }

    private static List<string> Check(FormatCounts counts, FormatExpectation expected, bool direct)
    {
        var errors = new List<string>();
        string status = direct || expected.WarningCount == 0 ? "Completed" : "Partial";
        if (counts.Status != status) errors.Add("status");
        int hits = direct ? 0 : expected.EngineOccurrences;
        if (counts.ScannedFiles != 1 || counts.PublishedFiles != hits || counts.Occurrences != hits) errors.Add("files-or-hits");
        if (counts.Segments != (direct ? expected.Segments : 0) || counts.TextCharacters != (direct ? expected.TextCharacters : 0))
            errors.Add("extraction-counts");
        if (counts.Warnings != expected.WarningCount || counts.Errors != 0 || counts.IssueDetails != (direct ? 0 : expected.WarningCount) ||
            counts.SkippedFiles != 0 || counts.SuppressedIssues != 0 || counts.LimitReason is not null) errors.Add("diagnostics");
        return errors;
    }

    private static OracleDigests Oracle(FormatCorpusFile file, FormatExpectation expected, bool quick)
    {
        using var segments = new OrderedDigest(); using var warnings = new OrderedDigest();
        using var results = new OrderedDigest(); using var issues = new OrderedDigest(); using var summaries = new OrderedDigest();
        TextSegment? final = null;
        foreach (var segment in OriginalSegments(file.Kind, quick)) { segments.Add(segment); final = segment; }
        string? warning = Warning(file.Kind);
        if (warning is not null)
        {
            warnings.Add(warning);
            issues.Add(new SearchIssue(Filename(file.Kind), warning, SearchIssueSeverity.Warning));
        }
        if (expected.EngineOccurrences == 1)
        {
            var segment = final!;
            int offset = segment.Text.IndexOf(Needle, StringComparison.Ordinal), end = offset + Needle.Length;
            if (offset < 0) throw new BenchmarkException("format-oracle-needle");
            var location = segment.Source.Kind == SourceKind.TextLine ? segment.Source with { Column = offset + 1 } : segment.Source;
            var match = new MatchItem(Needle, segment.Text[Math.Max(0, offset - Context)..offset], Needle,
                segment.Text.Substring(end, Math.Min(Context, segment.Text.Length - end)), location, segment.Id, offset);
            results.Add(new FileResult(Filename(file.Kind), Path.GetExtension(Filename(file.Kind)),
                new DateTime(file.LastWriteTimeUtcTicks, DateTimeKind.Utc), [match])
                { ExtractionWarnings = warning is null ? [] : [warning] });
        }
        int hits = expected.EngineOccurrences;
        summaries.Add(new SearchSummary(warning is null ? SearchStatus.Completed : SearchStatus.Partial,
            new SearchProgress(1, hits, hits, 0, Filename(file.Kind)))
            { Diagnostics = new SearchDiagnostics { WarningCount = warning is null ? 0 : 1 } });
        return new(segments.Finish(), warnings.Finish(), results.Finish(), issues.Finish(), summaries.Finish());
    }

    private static async Task<FormatRetainedObservation> ObserveRetained(string path, FormatExpectation expected)
    {
        var points = new List<FormatRetainedPoint>();
        var targets = new HashSet<long> { 1, Math.Max(1, expected.Segments / 2), Math.Max(1, expected.Segments * 3 / 4) };
        long count = 0, characters = 0;
        int warnings = 0;
        await using (var iterator = Extractor(expected.Kind).ExtractAsync(path, new ExtractionContext(_ => warnings++)).GetAsyncEnumerator())
        {
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                count++;
                // Synchronous helper prevents an async state-machine local retaining the previous segment.
                characters += CurrentLength(iterator);
                if (targets.Contains(count)) points.Add(new(count, expected.Segments, GC.GetTotalMemory(forceFullCollection: true)));
            }
        }
        return new("retained-consumption-excluded", count, characters, points.AsReadOnly(),
            count == expected.Segments && characters == expected.TextCharacters && warnings == expected.WarningCount && points.Count == targets.Count);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CurrentLength(IAsyncEnumerator<TextSegment> iterator) => iterator.Current.Text.Length;

    private static TextEncodingMode EncodingMode(FormatExpectation expected) => expected.Encoding == "Utf8OrGb18030"
        ? TextEncodingMode.Utf8OrGb18030 : TextEncodingMode.StrictUnicode;
    private static ITextExtractor Extractor(string kind) => kind switch
    {
        _ when IsText(kind) => new PlainTextExtractor(),
        _ when kind.StartsWith("csv-", StringComparison.Ordinal) => new CsvTextExtractor(),
        _ when kind.StartsWith("xlsx-", StringComparison.Ordinal) => new ExcelTextExtractor(),
        _ when kind.StartsWith("docx-", StringComparison.Ordinal) => new WordTextExtractor(),
        "pptx" => new PowerPointTextExtractor(), "pdf" => new PdfTextExtractor(),
        _ => throw new BenchmarkException("unknown-corpus-kind")
    };
    private static bool IsText(string kind) => kind.StartsWith("txt-", StringComparison.Ordinal) ||
        kind.StartsWith("md-", StringComparison.Ordinal) || kind.StartsWith("log-", StringComparison.Ordinal);
    private static string Filename(string kind) => "fixture." + kind.Split('-')[0];
    private static int Units(string kind, bool quick) => kind switch
    {
        _ when IsText(kind) => kind.EndsWith("small", StringComparison.Ordinal) ? (quick ? 2 : 4) : (quick ? 16 : 16384),
        "csv-plain" or "csv-quoted" => quick ? 4 : 4096,
        "csv-boundary" => quick ? 4 : 256,
        "xlsx-shared" or "xlsx-inline" => quick ? 8 : 80000,
        "docx-paragraphs" or "docx-tables" => quick ? 8 : 20000,
        "pptx" or "pdf" => quick ? 2 : 100,
        _ => throw new BenchmarkException("unknown-corpus-kind")
    };
    private static string Payload(int index, int length, bool final, bool unicode = true)
    {
        string prefix = (unicode ? "original orchard 文本 " : "original orchard text ") + index.ToString("D6", CultureInfo.InvariantCulture) + " ";
        string suffix = final ? " " + Needle + " tail" : " ordinary parcel";
        return prefix + new string((char)('a' + index % 23), length - prefix.Length - suffix.Length) + suffix;
    }
    private sealed record CsvRow(string Encoded, string[] Values, long[] Lines, int Newlines);
    private static CsvRow Row(string kind, bool final)
    {
        string[] fields = kind switch
        {
            "csv-plain" => ["original meadow", "plain parcel", ""],
            "csv-quoted" => ["orchard,amber", "a \"folded\" note", "first\r\nsecond\rtail\nend", ""],
            "csv-boundary" => [new string('b', 8190) + "\"" + new string('c', 8190) + "\r\nfold\rend", "edge,\"parcel\"", ""],
            _ => throw new BenchmarkException("unknown-corpus-kind")
        };
        bool quoted = kind != "csv-plain";
        string Encode(string value) => quoted ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
        string ending = kind == "csv-plain" ? "\n" : kind == "csv-boundary" && final ? "" : "\r\n";
        int size = kind == "csv-boundary" ? 32768 : 2048;
        int padding = size - (string.Join(',', fields.Select(Encode)) + ending).Length;
        string suffix = final ? " " + Needle : "";
        fields[^1] = new string('x', padding - suffix.Length) + suffix;
        string encoded = string.Join(',', fields.Select(Encode)) + ending;
        long line = 0; var offsets = new long[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            offsets[i] = line;
            fields[i] = fields[i].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            line += fields[i].Count(character => character == '\n');
        }
        return new(encoded, fields, offsets, checked((int)line + (ending.Length == 0 ? 0 : 1)));
    }
    private static IEnumerable<TextSegment> OriginalSegments(string kind, bool quick)
    {
        int units = Units(kind, quick);
        if (IsText(kind))
        {
            for (int index = 1; index <= units; index++)
                yield return new($"line:{index}", Payload(index, 1023, index == units), new SourceLocation(index, 1));
        }
        else if (kind.StartsWith("csv-", StringComparison.Ordinal))
        {
            long line = 1; var ordinary = Row(kind, false); var final = Row(kind, true);
            for (int record = 1; record <= units; record++)
            {
                var row = record == units ? final : ordinary;
                for (int field = 0; field < row.Values.Length; field++)
                    yield return new($"csv:{record}:{field + 1}", row.Values[field],
                        new SourceLocator(SourceKind.CsvField, Line: line + row.Lines[field], Record: record, Field: field + 1));
                line += row.Newlines;
            }
        }
        else if (kind.StartsWith("xlsx-", StringComparison.Ordinal))
        {
            for (int index = 1; index <= units; index++)
            {
                string address = "A" + index.ToString(CultureInfo.InvariantCulture);
                yield return new($"excel:s1:{address}", Payload(index, 64, index == units),
                    new SourceLocator(SourceKind.ExcelCell, Row: index, Cell: 1, Sheet: "Orchard", Address: address,
                        ValueKind: kind == "xlsx-shared" ? "共享字符串" : "内联字符串"));
            }
        }
        else if (kind.StartsWith("docx-", StringComparison.Ordinal))
        {
            for (int index = 1; index <= units; index++)
            {
                bool table = kind == "docx-tables"; int row = (index - 1) / 4 + 1, cell = (index - 1) % 4 + 1;
                yield return new(table ? $"word:t1:r{row}:c{cell}:p1" : $"word:p{index}", Payload(index, 192, index == units),
                    table ? new SourceLocator(SourceKind.WordTableCell, Paragraph: 1, Table: 1, Row: row, Cell: cell) :
                        new SourceLocator(SourceKind.WordParagraph, Paragraph: index));
            }
        }
        else if (kind == "pptx")
        {
            int paragraphs = quick ? 3 : 200;
            for (int slide = 1; slide <= units; slide++)
                for (int paragraph = 1; paragraph <= paragraphs; paragraph++)
                    yield return new($"ppt:s{slide}:o1:r:c:p{paragraph}",
                        Payload((slide - 1) * paragraphs + paragraph, 128, slide == units && paragraph == paragraphs),
                        new SourceLocator(SourceKind.SlideText, Paragraph: paragraph, Slide: slide, ObjectName: "Orchard frame"));
        }
        else if (kind == "pdf")
        {
            for (int page = 1; page <= units; page++)
                yield return new($"pdf:p{page}", Payload(page, quick ? 128 : 4096, page == units, unicode: false),
                    new SourceLocator(SourceKind.PdfPage, Page: page));
        }
        else throw new BenchmarkException("unknown-corpus-kind");
    }
    private static (long Segments, long Characters, int Maximum) Statistics(string kind, bool quick)
    {
        long count = 0, characters = 0; int maximum = 0;
        foreach (var segment in OriginalSegments(kind, quick))
        { count++; characters += segment.Text.Length; maximum = Math.Max(maximum, segment.Text.Length); }
        return (count, characters, maximum);
    }

    private static FormatCorpusFile Generate(string kind, bool quick, Stream? destination = null)
    {
        using var stream = new HashingSink(destination);
        if (IsText(kind))
        {
            foreach (var segment in OriginalSegments(kind, quick)) WriteUtf8(stream, segment.Text + "\n");
        }
        else if (kind.StartsWith("csv-", StringComparison.Ordinal))
        {
            var ordinary = Encoding.UTF8.GetBytes(Row(kind, false).Encoded); var final = Encoding.UTF8.GetBytes(Row(kind, true).Encoded);
            for (int index = 1, units = Units(kind, quick); index <= units; index++) stream.Write(index == units ? final : ordinary);
        }
        else if (kind == "pdf") WritePdf(stream, quick);
        else WritePackage(stream, kind, quick);
        var stats = Statistics(kind, quick);
        return new(kind, kind + "/" + Filename(kind), stream.Bytes, stats.Segments, stats.Characters, stats.Maximum,
            Warning(kind) is null ? 0 : 1, 0, stream.Finish());
    }

    private static void WritePackage(Stream stream, string kind, bool quick)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        bool excel = kind.StartsWith("xlsx-", StringComparison.Ordinal), word = kind.StartsWith("docx-", StringComparison.Ordinal);
        string main = excel ? "xl/workbook.xml" : word ? "word/document.xml" : "ppt/presentation.xml";
        var parts = new List<(string Path, string Type)>
        {
            (main, excel ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" : word ?
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" : "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml")
        };
        if (excel)
        {
            parts.Add(("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            if (kind == "xlsx-shared") parts.Add(("xl/sharedStrings.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"));
        }
        if (!excel && !word)
            for (int slide = 1; slide <= Units(kind, quick); slide++)
                parts.Add(($"ppt/slides/slide{slide}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"));
        Xml(zip, "[Content_Types].xml", x =>
        {
            x.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            x.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types"); x.WriteAttributeString("Extension", "rels"); x.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); x.WriteEndElement();
            foreach (var part in parts) { x.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types"); x.WriteAttributeString("PartName", "/" + part.Path); x.WriteAttributeString("ContentType", part.Type); x.WriteEndElement(); }
            x.WriteEndElement();
        });
        Relationships(zip, "_rels/.rels", [("rId1", R + "/officeDocument", main)]);
        if (excel)
        {
            Xml(zip, main, x =>
            {
                x.WriteStartElement("workbook", S); x.WriteAttributeString("xmlns", "r", null, R); x.WriteStartElement("sheets", S);
                x.WriteStartElement("sheet", S); x.WriteAttributeString("name", "Orchard"); x.WriteAttributeString("sheetId", "1"); x.WriteAttributeString("r", "id", R, "rId1"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
            });
            var relations = new List<(string, string, string)> { ("rId1", R + "/worksheet", "worksheets/sheet1.xml") };
            if (kind == "xlsx-shared") relations.Add(("rId2", R + "/sharedStrings", "sharedStrings.xml"));
            Relationships(zip, "xl/_rels/workbook.xml.rels", relations);
            Xml(zip, "xl/worksheets/sheet1.xml", x =>
            {
                x.WriteStartElement("worksheet", S); x.WriteStartElement("sheetData", S);
                int index = 0;
                foreach (var segment in OriginalSegments(kind, quick))
                {
                    index++; x.WriteStartElement("row", S); x.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
                    x.WriteStartElement("c", S); x.WriteAttributeString("r", segment.Source.Address); x.WriteAttributeString("t", kind == "xlsx-shared" ? "s" : "inlineStr");
                    if (kind == "xlsx-shared") x.WriteElementString("v", S, (index - 1).ToString(CultureInfo.InvariantCulture));
                    else { x.WriteStartElement("is", S); x.WriteElementString("t", S, segment.Text); x.WriteEndElement(); }
                    x.WriteEndElement(); x.WriteEndElement();
                }
                x.WriteEndElement(); x.WriteEndElement();
            });
            if (kind == "xlsx-shared") Xml(zip, "xl/sharedStrings.xml", x =>
            {
                x.WriteStartElement("sst", S);
                foreach (var segment in OriginalSegments(kind, quick)) { x.WriteStartElement("si", S); x.WriteElementString("t", S, segment.Text); x.WriteEndElement(); }
                x.WriteEndElement();
            });
        }
        else if (word) Xml(zip, main, x =>
        {
            x.WriteStartElement("w", "document", W); x.WriteStartElement("w", "body", W);
            bool table = kind == "docx-tables";
            if (table) x.WriteStartElement("w", "tbl", W);
            int index = 0;
            foreach (var segment in OriginalSegments(kind, quick))
            {
                if (table) { if (index % 4 == 0) x.WriteStartElement("w", "tr", W); x.WriteStartElement("w", "tc", W); }
                x.WriteStartElement("w", "p", W); x.WriteStartElement("w", "r", W); x.WriteElementString("w", "t", W, segment.Text); x.WriteEndElement(); x.WriteEndElement();
                if (table) { x.WriteEndElement(); if (index % 4 == 3) x.WriteEndElement(); }
                index++;
            }
            if (table) x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        });
        else
        {
            int units = Units(kind, quick), paragraphs = quick ? 3 : 200;
            Xml(zip, main, x =>
            {
                x.WriteStartElement("p", "presentation", P); x.WriteAttributeString("xmlns", "r", null, R); x.WriteStartElement("p", "sldIdLst", P);
                for (int slide = 1; slide <= units; slide++) { x.WriteStartElement("p", "sldId", P); x.WriteAttributeString("id", (255 + slide).ToString(CultureInfo.InvariantCulture)); x.WriteAttributeString("r", "id", R, "rId" + slide); x.WriteEndElement(); }
                x.WriteEndElement(); x.WriteEndElement();
            });
            Relationships(zip, "ppt/_rels/presentation.xml.rels", Enumerable.Range(1, units).Select(slide =>
                ("rId" + slide, R + "/slide", $"slides/slide{slide}.xml")));
            for (int slide = 1; slide <= units; slide++)
            {
                int current = slide;
                Xml(zip, $"ppt/slides/slide{slide}.xml", x =>
                {
                    x.WriteStartElement("p", "sld", P); x.WriteAttributeString("xmlns", "a", null, A); x.WriteStartElement("p", "cSld", P); x.WriteStartElement("p", "spTree", P);
                    x.WriteStartElement("p", "sp", P); x.WriteStartElement("p", "nvSpPr", P); x.WriteStartElement("p", "cNvPr", P); x.WriteAttributeString("id", "2"); x.WriteAttributeString("name", "Orchard frame"); x.WriteEndElement(); x.WriteElementString("p", "cNvSpPr", P, ""); x.WriteElementString("p", "nvPr", P, ""); x.WriteEndElement();
                    x.WriteElementString("p", "spPr", P, ""); x.WriteStartElement("p", "txBody", P); x.WriteElementString("a", "bodyPr", A, ""); x.WriteElementString("a", "lstStyle", A, "");
                    for (int paragraph = 1; paragraph <= paragraphs; paragraph++)
                    {
                        x.WriteStartElement("a", "p", A); x.WriteStartElement("a", "r", A);
                        x.WriteElementString("a", "t", A, Payload((current - 1) * paragraphs + paragraph, 128, current == units && paragraph == paragraphs));
                        x.WriteEndElement(); x.WriteEndElement();
                    }
                    x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
                });
            }
        }
    }
    private static void Xml(ZipArchive zip, string name, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false });
        write(writer);
    }
    private static void Relationships(ZipArchive zip, string path, IEnumerable<(string Id, string Type, string Target)> relationships) => Xml(zip, path, x =>
    {
        x.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        foreach (var relation in relationships) { x.WriteStartElement("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"); x.WriteAttributeString("Id", relation.Id); x.WriteAttributeString("Type", relation.Type); x.WriteAttributeString("Target", relation.Target); x.WriteEndElement(); }
        x.WriteEndElement();
    });

    // Original minimal PDF, one ASCII text operation per page: exact content order without layout ambiguity.
    private static void WritePdf(HashingSink stream, bool quick)
    {
        int pages = Units("pdf", quick);
        var offsets = new List<long> { 0 };
        void Object(string value)
        {
            offsets.Add(stream.Bytes);
            WriteUtf8(stream, $"{offsets.Count - 1} 0 obj\n{value}\nendobj\n");
        }
        WriteUtf8(stream, "%PDF-1.4\n%Original-format-fixture\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Count {pages} /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(index => $"{4 + index * 2} 0 R"))}] >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int page = 0;
        foreach (var segment in OriginalSegments("pdf", quick))
        {
            page++;
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (page - 1) * 2} 0 R >>");
            string text = segment.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
            string content = $"BT /F1 0.1 Tf 1 0 0 1 40 760 Tm ({text}) Tj ET\n";
            Object($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        long xref = stream.Bytes;
        WriteUtf8(stream, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) WriteUtf8(stream, offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        WriteUtf8(stream, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
    private static string? Warning(string kind) => kind switch
    {
        "xlsx-shared" or "xlsx-inline" => "Excel 仅搜索可见普通工作表；值为文件保存的原始值或公式缓存，不重算公式，也不代表 Excel 格式化显示值；批注、图表等不搜索。",
        "docx-paragraphs" or "docx-tables" => "Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。",
        "pptx" => "PowerPoint 仅搜索幻灯片顶层文本框及表格；备注、组合图形、母版、图表和 SmartArt 不搜索。",
        "pdf" => "PDF 按文件内容顺序提取每页文字；多栏、定位绘制文字的阅读顺序可能与视觉不同，OCR 未启用。",
        _ => null
    };
    private static string Relative(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new BenchmarkException("format-result-outside-scenario-root");
        return relative;
    }
    private static string Stamp(string root, FormatManifest manifest) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { manifest, marker = Source(Path.Combine(root, Marker)), metadata = Source(Path.Combine(root, Manifest)) })));
    private static FormatSourceStamp Source(string path)
    {
        Ordinary(path); var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, Hash(path));
    }
    private static void AssertSource(FormatCorpusFile file, FormatSourceStamp stamp)
    {
        if (stamp != new FormatSourceStamp(file.Bytes, file.LastWriteTimeUtcTicks, file.Sha256))
            throw new BenchmarkException("format-source-integrity-mismatch");
    }
    private static string Root(string root)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (path == Path.GetPathRoot(path)) throw new BenchmarkException("filesystem-root-not-allowed");
        OrdinaryAncestors(path); return path;
    }
    private static void OrdinaryAncestors(string path) => BenchmarkCorpus.AssertOrdinaryAncestors(path);
    private static void WriteNew(string path, string text)
    {
        OrdinaryAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); WriteUtf8(stream, text);
    }
    private static void WriteUtf8(Stream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));
    private static string Hash(string path)
    {
        Ordinary(path); using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Ordinary(string path)
    {
        OrdinaryAncestors(path);
        if (!OperatingSystem.IsWindows()) return;
        using var handle = File.OpenHandle(path);
        if (!GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()) || info.Links != 1)
            throw new BenchmarkException("format-linked-or-unreadable-file");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long Allocation, End; public uint Links; public byte DeletePending, Directory; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out StandardInfo info, uint size);
    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class OrderedDigest : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Add<T>(T value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value); Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); _hash.AppendData(length); _hash.AppendData(bytes);
        }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        public void Dispose() => _hash.Dispose();
    }
    // Force identical nonseekable ZIP output for actual files and provenance regeneration sinks.
    private sealed class HashingSink(Stream? destination) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Bytes { get; private set; }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { destination?.Write(buffer); _hash.AppendData(buffer); Bytes += buffer.Length; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() => destination?.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
