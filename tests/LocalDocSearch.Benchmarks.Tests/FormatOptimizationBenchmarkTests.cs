using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LocalDocSearch.Benchmarks;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

public sealed class FormatOptimizationBenchmarkTests(FormatQuickCorpus corpus) : IClassFixture<FormatQuickCorpus>
{
    private const string Term = "formatbenchneedle";
    private static readonly string EmptyDigest = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
    public static IEnumerable<object[]> AllScenarios => FormatOptimizationBenchmark.Scenarios.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public async Task EveryQuickScenarioHasIndependentCountsAndCompleteOrderedDigests(string scenario)
    {
        var report = await FormatOptimizationBenchmark.RunAsync(corpus.Root, scenario, 2);
        var expected = report.Expected;
        var segments = Segments(expected.Kind).ToArray();
        var (count, characters, maximum) = QuickCounts(expected.Kind);
        int hits = scenario.StartsWith("engine-sparse-", StringComparison.Ordinal) ? 1 : 0;
        string? warning = Warning(expected.Kind);
        string filename = "fixture." + expected.Kind.Split('-')[0];
        Assert.Equal(45, FormatOptimizationBenchmark.Scenarios.Count);
        Assert.True(report.Passed, string.Join(";", report.Verifications.SelectMany(item => item.Mismatches)));
        Assert.Equal(1, report.Workers);
        Assert.Equal(count, expected.Segments);
        Assert.Equal(characters, expected.TextCharacters);
        Assert.Equal(maximum, expected.MaxSegmentCharacters);
        Assert.Equal(count, segments.LongLength);
        Assert.Equal(characters, segments.Sum(item => (long)item.Text.Length));
        Assert.Equal(hits, expected.EngineOccurrences);
        Assert.Equal(scenario.EndsWith("-autoUTF8", StringComparison.Ordinal) ? "Utf8OrGb18030" : "StrictUnicode", expected.Encoding);
        Assert.Equal(report.SourceSha256Before, report.SourceSha256After);
        foreach (string hash in new[] { report.LoadedCoreSha256, report.LoadedExtractorsSha256, report.DriverSha256 })
            Assert.Matches("^[A-F0-9]{64}$", hash);
        Assert.Null(report.RetainedObservation);
        Assert.Equal(2, report.Rounds.Count);
        Assert.Equal(2, report.Verifications.Count);
        foreach (var round in report.Rounds)
        {
            Assert.True(round.Passed, string.Join(",", round.Mismatches));
            Assert.Equal(expected.Mode == "direct" || warning is null ? "Completed" : "Partial", round.Counts.Status);
            Assert.Equal(1, round.Counts.ScannedFiles);
            Assert.Equal(hits, round.Counts.PublishedFiles);
            Assert.Equal(hits, round.Counts.Occurrences);
            Assert.Equal(expected.Mode == "direct" ? count : 0, round.Counts.Segments);
            Assert.Equal(expected.Mode == "direct" ? characters : 0, round.Counts.TextCharacters);
            Assert.Equal(warning is null ? 0 : 1, round.Counts.Warnings);
            Assert.Equal(expected.Mode == "engine" && warning is not null ? 1 : 0, round.Counts.IssueDetails);
            Assert.Equal(0, round.Counts.Errors);
            if (expected.Mode == "direct" || hits == 1) Assert.NotNull(round.FirstResultMilliseconds);
            else Assert.Null(round.FirstResultMilliseconds);
            Assert.DoesNotContain("sha256", BenchmarkJson.Serialize(round), StringComparison.OrdinalIgnoreCase);
        }
        var final = segments[^1];
        int offset = final.Text.IndexOf(Term, StringComparison.Ordinal);
        var location = final.Source.Kind == SourceKind.TextLine ? final.Source with { Column = offset + 1 } : final.Source;
        var match = new MatchItem(Term, final.Text[Math.Max(0, offset - 12)..offset], Term,
            final.Text.Substring(offset + Term.Length, Math.Min(12, final.Text.Length - offset - Term.Length)), location, final.Id, offset);
        FileResult[] results = hits == 0 ? [] : [new(filename, Path.GetExtension(filename),
            File.GetLastWriteTimeUtc(Path.Combine(corpus.Root, expected.Kind, filename)), [match])
            { ExtractionWarnings = warning is null ? [] : [warning] }];
        SearchIssue[] issues = warning is null ? [] : [new(filename, warning, SearchIssueSeverity.Warning)];
        var summary = new SearchSummary(warning is null ? SearchStatus.Completed : SearchStatus.Partial,
            new SearchProgress(1, hits, hits, 0, filename))
            { Diagnostics = new SearchDiagnostics { WarningCount = warning is null ? 0 : 1 } };
        foreach (var check in report.Verifications)
        {
            Assert.True(check.Passed, string.Join(",", check.Mismatches));
            Assert.Equal("after-round-excluded", check.Phase);
            Assert.Equal(check.SourceBefore, check.SourceAfter);
            Assert.Equal(Digest(segments), check.SegmentSha256);
            Assert.Equal(check.SegmentSha256, check.ExpectedSegmentSha256);
            Assert.Equal(Digest(warning is null ? Array.Empty<string>() : [warning]), check.ExtractionWarningsSha256);
            Assert.Equal(check.ExtractionWarningsSha256, check.ExpectedExtractionWarningsSha256);
            Assert.Equal(Digest(results), check.EngineResultSha256);
            Assert.Equal(check.EngineResultSha256, check.ExpectedEngineResultSha256);
            Assert.Equal(Digest(issues), check.EngineIssuesSha256);
            Assert.Equal(check.EngineIssuesSha256, check.ExpectedEngineIssuesSha256);
            Assert.Equal(Digest(new[] { summary }), check.EngineSummarySha256);
            Assert.Equal(check.EngineSummarySha256, check.ExpectedEngineSummarySha256);
            if (hits == 0) Assert.Equal(EmptyDigest, check.EngineResultSha256);
        }
        string json = BenchmarkJson.Serialize(report);
        Assert.DoesNotContain(corpus.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Term, json, StringComparison.Ordinal);
        Assert.DoesNotContain("orchard", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all SHA", report.TimingSemantics, StringComparison.Ordinal);
        Assert.Contains("NOT per-round peak", report.PeakSemantics, StringComparison.Ordinal);
        Assert.Contains("ElapsedMilliseconds normalized to zero", report.DigestSemantics, StringComparison.Ordinal);
        Assert.Equal(corpus.Snapshot, Snapshot(corpus.Root));
    }

    [Fact]
    public void OriginalPackagesHaveNamespacedOpcPartsAndRepeatableBytes()
    {
        using var other = new FormatQuickCorpus();
        var first = FormatOptimizationBenchmark.OpenVerified(corpus.Root);
        var second = FormatOptimizationBenchmark.OpenVerified(other.Root);
        Assert.Equal(15, first.Files.Count);
        foreach (var file in first.Files)
        {
            Assert.Equal(second.Files.Single(item => item.Kind == file.Kind).Sha256, file.Sha256);
            var counts = QuickCounts(file.Kind);
            Assert.Equal(counts.Count, file.Segments);
            Assert.Equal(counts.Characters, file.TextCharacters);
            Assert.Equal(counts.Maximum, file.MaxSegmentCharacters);
            string path = Path.Combine(corpus.Root, file.RelativePath);
            Assert.Equal(file.Bytes, new FileInfo(path).Length);
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            if (file.Kind.StartsWith("csv-", StringComparison.Ordinal))
                Assert.Equal(file.Kind == "csv-boundary" ? 131072 : 8192, file.Bytes);
            if (file.Kind.StartsWith("txt-", StringComparison.Ordinal) || file.Kind.StartsWith("md-", StringComparison.Ordinal) || file.Kind.StartsWith("log-", StringComparison.Ordinal))
                Assert.Equal(counts.Count * 1028, file.Bytes);
            if (file.Kind.StartsWith("xlsx-", StringComparison.Ordinal) || file.Kind.StartsWith("docx-", StringComparison.Ordinal) || file.Kind == "pptx")
            {
                using var zip = ZipFile.OpenRead(path);
                using var types = zip.GetEntry("[Content_Types].xml")!.Open();
                var document = XDocument.Load(types);
                Assert.All(document.Root!.Elements(), element => Assert.Equal("http://schemas.openxmlformats.org/package/2006/content-types", element.Name.NamespaceName));
                foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
                {
                    using var stream = entry.Open();
                    Assert.All(XDocument.Load(stream).Root!.Elements(), element => Assert.Equal("http://schemas.openxmlformats.org/package/2006/relationships", element.Name.NamespaceName));
                }
            }
        }
        string boundary = File.ReadAllText(Path.Combine(corpus.Root, "csv-boundary", "fixture.csv"));
        Assert.Equal('"', boundary[8191]); Assert.Equal('"', boundary[8192]);
        Assert.Equal('\r', boundary[16383]); Assert.Equal('\n', boundary[16384]);
        Assert.EndsWith("\"", boundary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("txt-large", 16384L, 16760832L, 1023)]
    [InlineData("md-small", 4L, 4092L, 1023)]
    [InlineData("csv-plain", 12288L, 8376320L, 2018)]
    [InlineData("csv-quoted", 16384L, 8323072L, 1983)]
    [InlineData("csv-boundary", 768L, 8385026L, 16390)]
    [InlineData("xlsx-shared", 80000L, 5120000L, 64)]
    [InlineData("xlsx-inline", 80000L, 5120000L, 64)]
    [InlineData("docx-paragraphs", 20000L, 3840000L, 192)]
    [InlineData("docx-tables", 20000L, 3840000L, 192)]
    [InlineData("pptx", 20000L, 2560000L, 128)]
    [InlineData("pdf", 100L, 409600L, 4096)]
    public void StandardProfilesHaveIndependentBoundedCountsWithoutGeneratingLargeCorpus(string kind, long count, long characters, int maximum)
    {
        var expected = FormatOptimizationBenchmark.Expected("engine-sparse-" + kind, false);
        Assert.Equal(count, expected.Segments); Assert.Equal(characters, expected.TextCharacters);
        Assert.Equal(maximum, expected.MaxSegmentCharacters);
        Assert.InRange(count, 1, 200000);
        Assert.InRange(characters, 1, 32L * 1024 * 1024);
        Assert.InRange(maximum, 1, new SearchResourceLimits().MaxSegmentCharacters);
    }

    [Theory]
    [InlineData("direct-xlsx-shared")]
    [InlineData("direct-docx-tables")]
    [InlineData("direct-pptx")]
    [InlineData("direct-pdf")]
    public async Task RetainedObservationIsSeparateAndConsumesEverySegment(string scenario)
    {
        var report = await FormatOptimizationBenchmark.RunAsync(corpus.Root, scenario, 1, observeRetained: true);
        var observation = Assert.IsType<FormatRetainedObservation>(report.RetainedObservation);
        Assert.True(report.Passed); Assert.True(observation.Passed);
        Assert.Equal("retained-consumption-excluded", observation.Phase);
        Assert.Equal(report.Expected.Segments, observation.EnumeratedSegments);
        Assert.Equal(report.Expected.TextCharacters, observation.TextCharacters);
        Assert.Equal(new long[] { 1, report.Expected.Segments / 2, report.Expected.Segments * 3 / 4 }.Distinct().Order(), observation.Points.Select(item => item.CurrentSegment).Order());
        Assert.All(observation.Points, point => Assert.True(point.ManagedLiveBytes > 0));
        Assert.Contains("NOT extraction peak", observation.Semantics, StringComparison.Ordinal);
        Assert.Single(report.Rounds); Assert.Single(report.Verifications);
    }

    [Theory]
    [InlineData("format-run", "direct-csv-plain --repeat 0")]
    [InlineData("format-run", "direct-csv-plain --repeat 21")]
    [InlineData("format-run", "direct-csv-plain --repeat nope")]
    [InlineData("format-run", "direct-csv-plain --repeat")]
    [InlineData("format-run", "direct-csv-plain --repeat 1 --repeat 1")]
    [InlineData("format-run", "direct-csv-plain --workers 2")]
    [InlineData("format-run", "direct-csv-plain --output")]
    [InlineData("format-run", "unknown")]
    [InlineData("format-run", "")]
    [InlineData("format-prepare", "--quick --quick")]
    [InlineData("format-prepare", "--unknown")]
    public async Task InvalidCliArgumentsAreSanitizedAndDoNotCreateMissingRoots(string command, string options)
    {
        string root = Path.Combine(Path.GetTempPath(), "FormatMissing-" + Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        Assert.Equal(1, await BenchmarkCli.ExecuteAsync([command, root, .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries)], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain(root, output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("forged-sha")]
    [InlineData("mtime")]
    [InlineData("marker")]
    [InlineData("traversal")]
    [InlineData("count")]
    [InlineData("manifest-json")]
    [InlineData("extra-directory")]
    [InlineData("extra")]
    public async Task DamagedOrUnownedCorporaAreRejectedWithoutRepair(string damage)
    {
        using var damaged = new FormatQuickCorpus();
        string source = Path.Combine(damaged.Root, "txt-small", "fixture.txt");
        string metadata = Path.Combine(damaged.Root, FormatOptimizationBenchmark.Manifest);
        var manifest = FormatOptimizationBenchmark.OpenVerified(damaged.Root);
        if (damage is "body" or "forged-sha")
        {
            var mtime = File.GetLastWriteTimeUtc(source);
            var bytes = File.ReadAllBytes(source); bytes[0] = (byte)'Z';
            File.WriteAllBytes(source, bytes); File.SetLastWriteTimeUtc(source, mtime);
            if (damage == "forged-sha") File.WriteAllText(metadata, BenchmarkJson.Serialize(manifest with { Files = manifest.Files.Select(file =>
                file.Kind == "txt-small" ? file with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } : file).ToArray() }));
        }
        else if (damage == "mtime") File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(5));
        else if (damage == "marker") File.WriteAllText(Path.Combine(damaged.Root, FormatOptimizationBenchmark.Marker), "altered");
        else if (damage == "extra") File.WriteAllText(Path.Combine(damaged.Root, "txt-small", "extra.txt"), "fixture");
        else if (damage == "manifest-json") File.WriteAllText(metadata, "{");
        else if (damage == "extra-directory") Directory.CreateDirectory(Path.Combine(damaged.Root, "unowned"));
        else File.WriteAllText(metadata, BenchmarkJson.Serialize(manifest with { Files = manifest.Files.Select(file => file.Kind == "txt-small" ?
            damage == "traversal" ? file with { RelativePath = "../outside.txt" } : file with { Segments = 5 } : file).ToArray() }));
        var before = Snapshot(damaged.Root);
        await Assert.ThrowsAsync<BenchmarkException>(() => FormatOptimizationBenchmark.RunAsync(damaged.Root, "engine-none-txt-small", 1));
        Assert.Equal(before, Snapshot(damaged.Root));
    }

    [Fact]
    public async Task CliPrepareAndOutputUseFreshPathsAndExposeNoBody()
    {
        using var fresh = new FormatQuickCorpus(prepare: false);
        using var prepareOutput = new StringWriter();
        Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["format-prepare", fresh.Root, "--quick"], prepareOutput));
        using var parsed = JsonDocument.Parse(prepareOutput.ToString());
        Assert.Equal(45, parsed.RootElement.GetProperty("scenarios").GetArrayLength());
        string outputPath = fresh.Root + "-result.json";
        try
        {
            using var runOutput = new StringWriter();
            Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["format-run", fresh.Root, "direct-xlsx-inline", "--repeat", "1", "--output", outputPath], runOutput));
            Assert.Equal(runOutput.ToString().TrimEnd(), File.ReadAllText(outputPath));
            Assert.DoesNotContain(Term, runOutput.ToString(), StringComparison.Ordinal);
            using var duplicate = new StringWriter();
            Assert.Equal(1, await BenchmarkCli.ExecuteAsync(["format-run", fresh.Root, "direct-xlsx-inline", "--repeat", "1", "--output", outputPath], duplicate));
        }
        finally { if (File.Exists(outputPath)) File.Delete(outputPath); }
        Assert.Throws<BenchmarkException>(() => FormatOptimizationBenchmark.Prepare(fresh.Root, true));
        await Assert.ThrowsAsync<BenchmarkException>(() => FormatOptimizationBenchmark.RunAsync(fresh.Root, "direct-csv-plain", 1, observeRetained: true));
    }

    [Theory]
    [InlineData("xlsx-inline/fixture.xlsx")]
    [InlineData(FormatOptimizationBenchmark.Marker)]
    [InlineData(FormatOptimizationBenchmark.Manifest)]
    public void WindowsHardLinksAreRejectedForSourceAndOwnershipControls(string relative)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var linked = new FormatQuickCorpus();
        string link = linked.Root + "-hardlink";
        Assert.True(CreateHardLink(link, Path.Combine(linked.Root, relative), IntPtr.Zero));
        try { Assert.Throws<BenchmarkException>(() => FormatOptimizationBenchmark.OpenVerified(linked.Root)); }
        finally { File.Delete(link); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);

    private static (long Count, long Characters, int Maximum) QuickCounts(string kind) => kind switch
    {
        "txt-small" or "md-small" or "log-small" => (2, 2046, 1023),
        "txt-large" or "md-large" or "log-large" => (16, 16368, 1023),
        "csv-plain" => (12, 8180, 2018), "csv-quoted" => (16, 8128, 1983), "csv-boundary" => (12, 131018, 16390),
        "xlsx-shared" or "xlsx-inline" => (8, 512, 64), "docx-paragraphs" or "docx-tables" => (8, 1536, 192),
        "pptx" => (6, 768, 128), "pdf" => (2, 256, 128), _ => throw new ArgumentException(nameof(kind))
    };
    private static string Text(int index, int length, bool final, bool ascii = false)
    {
        string first = (ascii ? "original orchard text " : "original orchard 文本 ") + index.ToString("D6", CultureInfo.InvariantCulture) + " ";
        string last = final ? " formatbenchneedle tail" : " ordinary parcel";
        return first.PadRight(length - last.Length, (char)('a' + index % 23)) + last;
    }
    private static IEnumerable<TextSegment> Segments(string kind)
    {
        var counts = QuickCounts(kind);
        if (kind.StartsWith("csv-", StringComparison.Ordinal))
        {
            long line = 1;
            for (int record = 1; record <= 4; record++)
            {
                bool final = record == 4;
                string suffix = final ? " " + Term : "";
                string[] values = kind switch
                {
                    "csv-plain" => ["original meadow", "plain parcel", new string('x', 2018 - suffix.Length) + suffix],
                    "csv-quoted" => ["orchard,amber", "a \"folded\" note", "first\nsecond\ntail\nend", new string('x', 1983 - suffix.Length) + suffix],
                    _ => [new string('b', 8190) + "\"" + new string('c', 8190) + "\nfold\nend", "edge,\"parcel\"", new string('x', (final ? 16353 : 16351) - suffix.Length) + suffix]
                };
                for (int field = 0; field < values.Length; field++)
                {
                    long start = line;
                    yield return new($"csv:{record}:{field + 1}", values[field], new SourceLocator(SourceKind.CsvField, Line: start, Record: record, Field: field + 1));
                    line += values[field].Count(character => character == '\n');
                }
                if (!final || kind != "csv-boundary") line++;
            }
            yield break;
        }
        for (int index = 1; index <= counts.Count; index++)
        {
            bool final = index == counts.Count;
            if (kind is "xlsx-shared" or "xlsx-inline")
                yield return new($"excel:s1:A{index}", Text(index, 64, final), new SourceLocator(SourceKind.ExcelCell, Row: index, Cell: 1, Sheet: "Orchard", Address: "A" + index,
                    ValueKind: kind == "xlsx-shared" ? "共享字符串" : "内联字符串"));
            else if (kind == "docx-paragraphs")
                yield return new($"word:p{index}", Text(index, 192, final), new SourceLocator(SourceKind.WordParagraph, Paragraph: index));
            else if (kind == "docx-tables")
            {
                int row = (index - 1) / 4 + 1, cell = (index - 1) % 4 + 1;
                yield return new($"word:t1:r{row}:c{cell}:p1", Text(index, 192, final), new SourceLocator(SourceKind.WordTableCell, Paragraph: 1, Table: 1, Row: row, Cell: cell));
            }
            else if (kind == "pptx")
            {
                int slide = (index - 1) / 3 + 1, paragraph = (index - 1) % 3 + 1;
                yield return new($"ppt:s{slide}:o1:r:c:p{paragraph}", Text(index, 128, final), new SourceLocator(SourceKind.SlideText, Paragraph: paragraph, Slide: slide, ObjectName: "Orchard frame"));
            }
            else if (kind == "pdf") yield return new($"pdf:p{index}", Text(index, 128, final, ascii: true), new SourceLocator(SourceKind.PdfPage, Page: index));
            else yield return new($"line:{index}", Text(index, 1023, final), new SourceLocation(index, 1));
        }
    }
    private static string? Warning(string kind) => kind switch
    {
        "xlsx-shared" or "xlsx-inline" => "Excel 仅搜索可见普通工作表；值为文件保存的原始值或公式缓存，不重算公式，也不代表 Excel 格式化显示值；批注、图表等不搜索。",
        "docx-paragraphs" or "docx-tables" => "Word 位置为正文结构提示，并非页码；页眉页脚、批注、文本框及修订删除文字不搜索。",
        "pptx" => "PowerPoint 仅搜索幻灯片顶层文本框及表格；备注、组合图形、母版、图表和 SmartArt 不搜索。",
        "pdf" => "PDF 按文件内容顺序提取每页文字；多栏、定位绘制文字的阅读顺序可能与视觉不同，OCR 未启用。", _ => null
    };
    private static string Digest<T>(IEnumerable<T> frames)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var frame in frames)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame); byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length); digest.AppendData(length); digest.AppendData(bytes);
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }
    internal static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Directory.Exists(path) ? "directory|" + Path.GetRelativePath(root, path) :
            string.Join("|", Path.GetRelativePath(root, path), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
}

public sealed class FormatQuickCorpus : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FormatBenchmark-" + Guid.NewGuid().ToString("N"));
    public string[] Snapshot { get; }
    public FormatQuickCorpus() : this(prepare: true) { }
    internal FormatQuickCorpus(bool prepare)
    {
        if (prepare) FormatOptimizationBenchmark.Prepare(Root, true);
        Snapshot = prepare ? FormatOptimizationBenchmarkTests.Snapshot(Root) : [];
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
