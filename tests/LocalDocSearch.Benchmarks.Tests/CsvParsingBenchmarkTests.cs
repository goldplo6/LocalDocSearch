using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalDocSearch.Benchmarks;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

public sealed class CsvParsingBenchmarkTests
{
    private static readonly string EmptyDigest = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
    public static IEnumerable<object[]> EveryScenario => CsvParsingBenchmark.Scenarios.Select(scenario => new object[] { scenario });

    [Theory]
    [MemberData(nameof(EveryScenario))]
    public async Task EachQuickScenarioCountsRealResultsAndVerifiesAfterEveryRound(string scenario)
    {
        using var corpus = new OwnedCorpus();
        string[] before = Snapshot(corpus.Root);
        var report = await CsvParsingBenchmark.RunAsync(corpus.Root, scenario, 2);
        Assert.True(report.Passed);
        Assert.Equal(9, CsvParsingBenchmark.Scenarios.Count);
        Assert.Equal(1, report.Workers);
        Assert.Equal(report.SourceSha256Before, report.SourceSha256After);
        Assert.Matches("^[A-F0-9]{64}$", report.CoreSha256);
        Assert.Matches("^[A-F0-9]{64}$", report.DriverSha256);
        Assert.Equal(2, report.Rounds.Count);
        Assert.Equal(2, report.Verifications.Count);
        string kind = scenario[(scenario.LastIndexOf('-') + 1)..];
        (long bytes, long records, long fields, long characters, int maximum) = QuickCounts(kind);
        Assert.Equal(bytes, report.Expected.Bytes);
        Assert.Equal(records, report.Expected.RecordCount);
        Assert.Equal(fields, report.Expected.FieldCount);
        Assert.Equal(characters, report.Expected.TextCharacters);
        Assert.Equal(maximum, report.Expected.MaxFieldCharacters);
        int hits = scenario.StartsWith("engine-sparse-", StringComparison.Ordinal) ? 1 : 0;
        bool direct = scenario.StartsWith("direct-", StringComparison.Ordinal);
        Assert.Equal(hits, report.Expected.EngineOccurrences);
        foreach (var round in report.Rounds)
        {
            Assert.True(round.Passed, string.Join(",", round.Mismatches));
            Assert.Equal("Completed", round.Counts.Status);
            Assert.Equal(1, round.Counts.ScannedFiles);
            Assert.Equal(direct ? fields : 0, round.Counts.SegmentCount);
            Assert.Equal(direct ? records : 0, round.Counts.RecordCount);
            Assert.Equal(direct ? characters : 0, round.Counts.TextCharacters);
            Assert.Equal(hits, round.Counts.Occurrences);
            Assert.Equal(hits, round.Counts.PublishedFiles);
            Assert.Equal(0, round.Counts.Errors);
            Assert.Equal(0, round.Counts.Warnings);
            Assert.Equal(0, round.Counts.IssueDetails);
            if (direct || hits == 1) Assert.NotNull(round.FirstResultMilliseconds);
            else Assert.Null(round.FirstResultMilliseconds);
            Assert.DoesNotContain("sha256", BenchmarkJson.Serialize(round), StringComparison.OrdinalIgnoreCase);
        }
        string directory = Path.Combine(corpus.Root, kind);
        var actualSegments = new List<TextSegment>();
        var actualWarnings = new List<string>();
        await foreach (var segment in new CsvTextExtractor().ExtractAsync(Path.Combine(directory, "original.csv"), new ExtractionContext(actualWarnings.Add)))
            actualSegments.Add(segment);
        Assert.Empty(actualWarnings);
        Assert.Equal(fields, actualSegments.Count);
        Assert.Equal(characters, actualSegments.Sum(segment => (long)segment.Text.Length));
        Assert.Equal(records, actualSegments[^1].Source.Record);
        string actualSegmentDigest = Digest(actualSegments);
        var final = actualSegments[^1];
        FileResult[] expectedResults = hits == 0 ? [] : [new FileResult("original.csv", ".csv",
            File.GetLastWriteTimeUtc(Path.Combine(directory, "original.csv")),
            [new MatchItem("csvbenchneedle", "", "csvbenchneedle", "", final.Source, final.Id, final.Text.Length - 14)])];
        string engineDigest = Digest(expectedResults);
        foreach (var verification in report.Verifications)
        {
            Assert.True(verification.Passed, string.Join(",", verification.Mismatches));
            Assert.Equal("after-round-excluded", verification.Phase);
            Assert.Equal(fields, verification.DirectCounts.SegmentCount);
            Assert.Equal(records, verification.DirectCounts.RecordCount);
            Assert.Equal(characters, verification.DirectCounts.TextCharacters);
            Assert.Equal(1, verification.EngineCounts.ScannedFiles);
            Assert.Equal(hits, verification.EngineCounts.PublishedFiles);
            Assert.Equal(hits, verification.EngineCounts.Occurrences);
            Assert.Equal(actualSegmentDigest, verification.SegmentSha256);
            Assert.Equal(actualSegmentDigest, verification.ExpectedSegmentSha256);
            Assert.Equal(engineDigest, verification.EngineResultSha256);
            Assert.Equal(engineDigest, verification.ExpectedEngineResultSha256);
            Assert.Equal(EmptyDigest, verification.ExtractionWarningsSha256);
            Assert.Equal(EmptyDigest, verification.EngineIssuesSha256);
        }
        Assert.Equal(new[] { 1, 2 }, report.Verifications.Select(item => item.Round));
        Assert.Equal(before, Snapshot(corpus.Root));
        string json = BenchmarkJson.Serialize(report);
        Assert.DoesNotContain(corpus.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("csvbenchneedle", json, StringComparison.Ordinal);
        Assert.DoesNotContain("orchard", json, StringComparison.Ordinal);
        Assert.Contains("open/read/decode/parse", report.TimingSemantics, StringComparison.Ordinal);
        Assert.Contains("including preceding rounds and their verification", report.PeakSemantics, StringComparison.Ordinal);
        Assert.Contains("replace only LocalDocSearch.Core.dll", report.ComparisonSemantics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareProducesOnlyOwnedOriginalFixturesAndRecordsActualSizes()
    {
        using var first = new OwnedCorpus();
        using var second = new OwnedCorpus(prepare: false);
        using var output = new StringWriter();
        Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["csv-prepare", second.Root, "--quick"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain(second.Root, output.ToString(), StringComparison.Ordinal);
        var manifest = CsvParsingBenchmark.OpenVerified(first.Root);
        var other = CsvParsingBenchmark.OpenVerified(second.Root);
        Assert.True(manifest.Quick);
        Assert.Equal(CsvParsingBenchmark.Generator, manifest.Generator);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal(256 * 1024, manifest.Files.Sum(file => file.Bytes));
        foreach (var file in manifest.Files)
        {
            var expected = QuickCounts(file.Kind);
            Assert.Equal(expected.Bytes, file.Bytes);
            Assert.Equal(expected.Records, file.RecordCount);
            Assert.Equal(expected.Fields, file.FieldCount);
            Assert.Equal(expected.Characters, file.TextCharacters);
            Assert.Equal(expected.Maximum, file.MaxFieldCharacters);
            string path = Path.Combine(first.Root, file.RelativePath);
            Assert.Equal(file.Bytes, new FileInfo(path).Length);
            Assert.Equal(file.LastWriteTimeUtcTicks, File.GetLastWriteTimeUtc(path).Ticks);
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            Assert.Equal(file.Sha256, other.Files.Single(item => item.Kind == file.Kind).Sha256);
            Assert.True(file.MaxFieldCharacters < new SearchResourceLimits().MaxSegmentCharacters);
        }
        var quoted = new List<TextSegment>();
        await foreach (var segment in new CsvTextExtractor().ExtractAsync(Path.Combine(first.Root, "quoted", "original.csv"), new ExtractionContext(_ => { })))
            quoted.Add(segment);
        Assert.Equal("orchard,blue", quoted[0].Text);
        Assert.Equal("a \"quoted\" note", quoted[1].Text);
        Assert.Equal("line-one\nline-two\ntail\nend", quoted[2].Text);
        Assert.Equal(new long[] { 1, 1, 1, 4, 5 }, quoted.Take(5).Select(segment => segment.Source.Line));
        string boundary = File.ReadAllText(Path.Combine(first.Root, "boundary", "original.csv"), Encoding.UTF8);
        Assert.Equal('"', boundary[8191]);
        Assert.Equal('"', boundary[8192]);
        Assert.Equal('\r', boundary[16383]);
        Assert.Equal('\n', boundary[16384]);
        Assert.EndsWith("csvbenchneedle\"", boundary, StringComparison.Ordinal);
        Assert.Throws<BenchmarkException>(() => CsvParsingBenchmark.Prepare(first.Root, true));
        Directory.CreateDirectory(second.Root + "-empty");
        try { Assert.Throws<BenchmarkException>(() => CsvParsingBenchmark.Prepare(second.Root + "-empty", true)); }
        finally { Directory.Delete(second.Root + "-empty"); }
    }

    [Theory]
    [InlineData("plain", 4096L, 16384L, 8372224L, 1759)]
    [InlineData("quoted", 2048L, 8192L, 8355840L, 4027)]
    [InlineData("boundary", 256L, 768L, 8385026L, 16390)]
    public void StandardProfileIsExactlyEightMiBAndInsideDefaultLimits(string kind, long records, long fields, long characters, int maximum)
    {
        var expected = CsvParsingBenchmark.Expected("direct-" + kind, false);
        Assert.Equal(8L * 1024 * 1024, expected.Bytes);
        Assert.Equal(records, expected.RecordCount);
        Assert.Equal(fields, expected.FieldCount);
        Assert.Equal(characters, expected.TextCharacters);
        Assert.Equal(maximum, expected.MaxFieldCharacters);
        Assert.True(maximum < new SearchResourceLimits().MaxSegmentCharacters);
    }

    [Theory]
    [InlineData("csv-run", "direct-plain --repeat 0")]
    [InlineData("csv-run", "direct-plain --repeat 21")]
    [InlineData("csv-run", "direct-plain --repeat -1")]
    [InlineData("csv-run", "direct-plain --repeat nope")]
    [InlineData("csv-run", "direct-plain --repeat")]
    [InlineData("csv-run", "direct-plain --repeat 1 --repeat 1")]
    [InlineData("csv-run", "direct-plain --workers 1")]
    [InlineData("csv-run", "direct-plain --output")]
    [InlineData("csv-run", "direct-plain --unknown")]
    [InlineData("csv-run", "unknown")]
    [InlineData("csv-run", "")]
    [InlineData("csv-prepare", "--quick --quick")]
    [InlineData("csv-prepare", "--unknown")]
    public async Task InvalidArgumentsAreJsonOnlyAndDoNotTouchMissingRoot(string command, string options)
    {
        string root = Path.Combine(Path.GetTempPath(), "CsvMissing-" + Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        Assert.Equal(1, await BenchmarkCli.ExecuteAsync([command, root, .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries)], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.False(Directory.Exists(root));
        Assert.DoesNotContain(root, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownScenarioNeverEchoesPersonalPath()
    {
        using var output = new StringWriter();
        string personal = Path.Combine(Path.GetTempPath(), "CsvPrivate-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(1, await BenchmarkCli.ExecuteAsync(["csv-run", personal, personal], output));
        Assert.DoesNotContain(personal, output.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("unknown-scenario", json.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("same-size-same-mtime")]
    [InlineData("forged-manifest")]
    [InlineData("mtime")]
    [InlineData("marker")]
    [InlineData("manifest-json")]
    [InlineData("manifest-count")]
    [InlineData("traversal")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("extra-nested-file")]
    public async Task ChangedOrUnownedCorpusIsRejectedWithoutRepair(string damage)
    {
        using var corpus = new OwnedCorpus();
        string source = Path.Combine(corpus.Root, "plain", "original.csv");
        string manifestPath = Path.Combine(corpus.Root, CsvParsingBenchmark.Manifest);
        switch (damage)
        {
            case "body": File.AppendAllText(source, "changed"); break;
            case "same-size-same-mtime":
            case "forged-manifest":
                var manifest = CsvParsingBenchmark.OpenVerified(corpus.Root);
                var modified = File.GetLastWriteTimeUtc(source);
                byte[] bytes = File.ReadAllBytes(source);
                bytes[0] = (byte)'z';
                File.WriteAllBytes(source, bytes);
                File.SetLastWriteTimeUtc(source, modified);
                if (damage == "forged-manifest")
                    File.WriteAllText(manifestPath, BenchmarkJson.Serialize(manifest with { Files = manifest.Files.Select(file =>
                        file.Kind == "plain" ? file with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } : file).ToArray() }));
                break;
            case "mtime": File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(10)); break;
            case "marker": File.WriteAllText(Path.Combine(corpus.Root, CsvParsingBenchmark.Marker), "changed"); break;
            case "manifest-json": File.WriteAllText(manifestPath, "{"); break;
            case "manifest-count":
            case "traversal":
                var original = CsvParsingBenchmark.OpenVerified(corpus.Root);
                var files = original.Files.ToArray();
                files[0] = damage == "traversal" ? files[0] with { RelativePath = "../outside.csv" } :
                    files[0] with { FieldCount = files[0].FieldCount + 1 };
                File.WriteAllText(manifestPath, BenchmarkJson.Serialize(original with { Files = files }));
                break;
            case "extra-file": File.WriteAllText(Path.Combine(corpus.Root, "extra.csv"), "original,test"); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(corpus.Root, "extra")); break;
            case "extra-nested-file": File.WriteAllText(Path.Combine(corpus.Root, "plain", "extra.csv"), "original,test"); break;
        }
        string[] before = Snapshot(corpus.Root);
        await Assert.ThrowsAsync<BenchmarkException>(() => CsvParsingBenchmark.RunAsync(corpus.Root, "direct-plain", 1));
        Assert.Equal(before, Snapshot(corpus.Root));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("existing")]
    [InlineData("inside")]
    [InlineData("missing-parent")]
    [InlineData("duplicate")]
    public async Task OutputUsesCreateNewOutsideCorpusAndExistingParent(string kind)
    {
        using var corpus = new OwnedCorpus();
        string target = kind == "inside" ? Path.Combine(corpus.Root, "result.json") :
            Path.Combine(Path.GetTempPath(), "CsvReport-" + Guid.NewGuid().ToString("N") + (kind == "missing-parent" ? "/result.json" : ".json"));
        if (kind == "existing") File.WriteAllText(target, "preserve");
        try
        {
            using var output = new StringWriter();
            string[] args = ["csv-run", corpus.Root, "engine-sparse-boundary", "--repeat", "1", "--output", target];
            if (kind == "duplicate") args = [.. args, "--output", target];
            Assert.Equal(kind == "new" ? 0 : 1, await BenchmarkCli.ExecuteAsync(args, output));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(kind == "new", json.RootElement.GetProperty("passed").GetBoolean());
            if (kind == "new") Assert.Equal(output.ToString().TrimEnd(), File.ReadAllText(target));
            else if (kind == "existing") Assert.Equal("preserve", File.ReadAllText(target));
            else Assert.False(File.Exists(target));
            Assert.DoesNotContain(target, output.ToString(), StringComparison.Ordinal);
        }
        finally { if (kind != "inside" && File.Exists(target)) File.Delete(target); }
    }

    [Theory]
    [InlineData("plain/original.csv")]
    [InlineData(CsvParsingBenchmark.Marker)]
    [InlineData(CsvParsingBenchmark.Manifest)]
    public void WindowsHardLinksAreRejectedForSourceAndControls(string relative)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var corpus = new OwnedCorpus();
        string link = Path.Combine(Path.GetTempPath(), "CsvLink-" + Guid.NewGuid().ToString("N"));
        Assert.True(CreateHardLink(link, Path.Combine(corpus.Root, relative), IntPtr.Zero));
        try { Assert.Throws<BenchmarkException>(() => CsvParsingBenchmark.OpenVerified(corpus.Root)); }
        finally { File.Delete(link); }
    }

    [Fact]
    public async Task HelpPublishesFrozenCsvCommandsAndScenarios()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["--help"], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(CsvParsingBenchmark.Scenarios,
            json.RootElement.GetProperty("csvScenarios").EnumerateArray().Select(item => item.GetString()!));
        var commands = json.RootElement.GetProperty("commands").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("csv-prepare <new-root> [--quick]", commands);
        Assert.Contains("csv-run <owned-root> <scenario> [--repeat 1..20] [--output new-json-file]", commands);
    }

    private static (long Bytes, long Records, long Fields, long Characters, int Maximum) QuickCounts(string kind) => kind switch
    {
        "plain" => (65536, 32, 128, 65408, 1759),
        "quoted" => (65536, 16, 64, 65280, 4027),
        "boundary" => (131072, 4, 12, 131018, 16390),
        _ => throw new InvalidOperationException("unexpected test corpus")
    };
    private static string Digest<T>(IEnumerable<T> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] prefix = new byte[4];
        foreach (var value in values)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            hash.AppendData(prefix);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path) ? "directory:" + path :
            path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);
    private sealed class OwnedCorpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CsvOwned-" + Guid.NewGuid().ToString("N"));
        public OwnedCorpus(bool prepare = true) { if (prepare) CsvParsingBenchmark.Prepare(Root, true); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
