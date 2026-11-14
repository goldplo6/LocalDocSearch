using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Benchmarks;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

public sealed class LiteralPrefilterBenchmarkTests
{
    [Theory]
    [InlineData("none-4", 0)]
    [InlineData("none-16", 0)]
    [InlineData("none-64", 0)]
    [InlineData("sparse-16", 16)]
    [InlineData("all-16", 128)]
    [InlineData("single-long", 0)]
    [InlineData("short-16", 0)]
    [InlineData("short-32", 0)]
    [InlineData("short-64", 0)]
    [InlineData("regex", 0)]
    [InlineData("none-32", 0)]
    [InlineData("sparse-32", 16)]
    [InlineData("all-32", 128)]
    [InlineData("sparse-64", 16)]
    [InlineData("all-64", 128)]
    [InlineData("unicode-none-64", 0)]
    [InlineData("unicode-sparse-64", 16)]
    public async Task EveryFrozenScenarioChecksCountsAndHashesOutsidePerformanceRounds(string scenario, int hits)
    {
        using var corpus = new OwnedCorpus();
        var report = await LiteralPrefilterBenchmark.RunAsync(corpus.Root, scenario, 2);
        Assert.True(report.Passed);
        Assert.Equal(17, LiteralPrefilterBenchmark.Scenarios.Count);
        Assert.Equal(1, report.Workers);
        Assert.Equal(hits, report.Expected.Occurrences);
        Assert.Equal(hits > 0 ? 1 : 0, report.Expected.MatchedFiles);
        Assert.Equal("Completed", report.Expected.Status);
        Assert.Equal(report.SourceSha256Before, report.SourceSha256After);
        Assert.Matches("^[A-F0-9]{64}$", report.CoreSha256);
        Assert.Matches("^[A-F0-9]{64}$", report.DriverSha256);
        Assert.Equal(2, report.Rounds.Count);
        foreach (var round in report.Rounds.Append(report.ResultVerification))
        {
            Assert.True(round.Passed, string.Join(",", round.Mismatches));
            Assert.Equal("Completed", round.Status);
            Assert.Equal(1, round.ScannedFiles);
            Assert.Equal(hits, round.Occurrences);
            Assert.Equal(hits, round.PublishedOccurrences);
            Assert.Equal(hits > 0 ? 1 : 0, round.PublishedFiles);
        }
        Assert.All(report.Rounds, round =>
        {
            Assert.Null(round.ResultOrderSha256);
            Assert.Null(round.ResultContentSha256);
        });
        Assert.Equal("verification-excluded", report.ResultVerification.Phase);
        Assert.Matches("^[A-F0-9]{64}$", report.ResultVerification.ResultContentSha256!);
        Assert.Matches("^[A-F0-9]{64}$", report.ResultVerification.ResultOrderSha256!);
        Assert.Contains("request/query creation included", report.TimingSemantics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickCorpusIsOriginalDeterministicAndFullyRecorded()
    {
        using var first = new OwnedCorpus();
        using var second = new OwnedCorpus(prepare: false);
        using var output = new StringWriter();
        Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["literal-prepare", second.Root, "--quick"], output));
        using var prepared = JsonDocument.Parse(output.ToString());
        Assert.True(prepared.RootElement.GetProperty("passed").GetBoolean());
        var manifest = LiteralPrefilterBenchmark.OpenVerified(first.Root);
        var other = LiteralPrefilterBenchmark.OpenVerified(second.Root);
        Assert.True(manifest.Quick);
        Assert.Equal(5, manifest.Files.Count);
        Assert.Equal(320 * 1024, manifest.Files.Sum(file => file.Bytes));
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(first.Root, file.RelativePath);
            Assert.Equal(65536, file.Bytes);
            Assert.Equal(new FileInfo(path).LastWriteTimeUtc.Ticks, file.LastWriteTimeUtcTicks);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file.Sha256);
            Assert.Equal(other.Files.Single(item => item.Scenario == file.Scenario).Sha256, file.Sha256);
        }
        foreach (string scenario in LiteralPrefilterBenchmark.Scenarios)
            Assert.Equal(scenario.StartsWith("all-", StringComparison.Ordinal) ? 4096 :
                scenario.StartsWith("sparse-", StringComparison.Ordinal) || scenario == "unicode-sparse-64" ? 16 : 0,
                LiteralPrefilterBenchmark.Expected(scenario, false).Occurrences);
        Assert.Throws<BenchmarkException>(() => LiteralPrefilterBenchmark.Prepare(first.Root, true));
    }

    [Theory]
    [InlineData("literal-run", "none-16 --repeat 0")]
    [InlineData("literal-run", "none-16 --repeat 21")]
    [InlineData("literal-run", "none-16 --repeat -1")]
    [InlineData("literal-run", "none-16 --repeat nope")]
    [InlineData("literal-run", "none-16 --repeat")]
    [InlineData("literal-run", "none-16 --repeat 1 --repeat 1")]
    [InlineData("literal-run", "none-16 --workers 1")]
    [InlineData("literal-run", "none-16 --output")]
    [InlineData("literal-run", "unknown")]
    [InlineData("literal-run", "")]
    [InlineData("literal-prepare", "--quick --quick")]
    [InlineData("literal-prepare", "--unknown")]
    public async Task InvalidArgumentsProduceOnlyJsonAndNeverCreateCorpus(string command, string options)
    {
        string root = Path.Combine(Path.GetTempPath(), "LiteralMissing-" + Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        string[] args = [command, root, .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Equal(1, await BenchmarkCli.ExecuteAsync(args, output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.False(Directory.Exists(root));
        Assert.DoesNotContain(root, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("mtime")]
    [InlineData("marker")]
    [InlineData("traversal")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    public async Task CorruptCorpusFailsWithoutRepair(string damage)
    {
        using var corpus = new OwnedCorpus();
        string source = Path.Combine(corpus.Root, "none", "original.txt");
        switch (damage)
        {
            case "body": File.AppendAllText(source, "changed"); break;
            case "mtime": File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(10)); break;
            case "marker": File.WriteAllText(Path.Combine(corpus.Root, LiteralPrefilterBenchmark.Marker), "changed"); break;
            case "traversal":
                var manifest = LiteralPrefilterBenchmark.OpenVerified(corpus.Root);
                var files = manifest.Files.ToArray();
                files[0] = files[0] with { RelativePath = "../outside.txt" };
                File.WriteAllText(Path.Combine(corpus.Root, LiteralPrefilterBenchmark.Manifest),
                    BenchmarkJson.Serialize(manifest with { Files = files }));
                break;
            case "extra-file": File.WriteAllText(Path.Combine(corpus.Root, "extra.txt"), "owned-test-only"); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(corpus.Root, "extra")); break;
        }
        string[] before = Snapshot(corpus.Root);
        await Assert.ThrowsAsync<BenchmarkException>(() => LiteralPrefilterBenchmark.RunAsync(corpus.Root, "none-16", 1));
        Assert.Equal(before, Snapshot(corpus.Root));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("existing")]
    [InlineData("inside")]
    public async Task CliOutputIsNewOutsideCorpusAndJsonOnly(string kind)
    {
        using var corpus = new OwnedCorpus();
        string target = kind == "inside" ? Path.Combine(corpus.Root, "result.json") :
            Path.Combine(Path.GetTempPath(), "LiteralReport-" + Guid.NewGuid().ToString("N") + ".json");
        if (kind == "existing") File.WriteAllText(target, "preserve");
        try
        {
            using var output = new StringWriter();
            int exit = await BenchmarkCli.ExecuteAsync(["literal-run", corpus.Root, "all-16", "--repeat", "1", "--output", target], output);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(kind == "new" ? 0 : 1, exit);
            if (kind == "new") Assert.Equal(output.ToString().TrimEnd(), File.ReadAllText(target));
            else if (kind == "existing") Assert.Equal("preserve", File.ReadAllText(target));
            else Assert.False(File.Exists(target));
        }
        finally { if (kind != "inside" && File.Exists(target)) File.Delete(target); }
    }

    [Fact]
    public void WindowsHardLinksAreRejectedBeforeSourceReading()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var corpus = new OwnedCorpus();
        string link = Path.Combine(Path.GetTempPath(), "LiteralLink-" + Guid.NewGuid().ToString("N"));
        Assert.True(CreateHardLink(link, Path.Combine(corpus.Root, "none", "original.txt"), IntPtr.Zero));
        try { Assert.Throws<BenchmarkException>(() => LiteralPrefilterBenchmark.OpenVerified(corpus.Root)); }
        finally { File.Delete(link); }
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);
    private sealed class OwnedCorpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LiteralOwned-" + Guid.NewGuid().ToString("N"));
        public OwnedCorpus(bool prepare = true) { if (prepare) LiteralPrefilterBenchmark.Prepare(Root, true); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
