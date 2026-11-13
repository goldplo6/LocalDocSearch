using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Benchmarks;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

[CollectionDefinition(Name)]
public sealed class CorpusCollection : ICollectionFixture<OriginalCorpusFixture>
{
    public const string Name = "original-benchmark-corpus";
}

public sealed class OriginalCorpusFixture : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Benchmarks.Tests-" + Guid.NewGuid().ToString("N"));
    public string Root { get; }
    public CorpusManifest Manifest { get; }

    public OriginalCorpusFixture()
    {
        Root = Path.Combine(_temporary, "owned-corpus");
        Manifest = BenchmarkCorpus.Prepare(Root, new PrepareOptions(Quick: true));
    }

    public void Dispose()
    {
        // Only the fixture-owned unique temporary directory is removed; production CLI has no delete.
        if (Directory.Exists(_temporary) && File.Exists(Path.Combine(Root, BenchmarkCorpus.MarkerName)))
            Directory.Delete(_temporary, recursive: true);
    }
}

[Collection(CorpusCollection.Name)]
public sealed class BenchmarkTests(OriginalCorpusFixture fixture)
{
    [Fact]
    public void OriginalQuickCorpusHasBoundedRecordedShapes()
    {
        var manifest = fixture.Manifest;
        Assert.Equal(10, manifest.Scenarios.Count);
        Assert.Equal("quick", manifest.Profile);
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
        var small = manifest.Scenarios.Single(scenario => scenario.Scenario == "many-small");
        Assert.Equal(200, small.Files);
        Assert.Equal(400, small.Occurrences);
        var mixed = manifest.Scenarios.Single(scenario => scenario.Scenario == "mixed");
        Assert.Equal(200, mixed.Files);
        Assert.Equal(400, mixed.Occurrences);
        Assert.Equal(100, mixed.Warnings);
        Assert.Equal("Partial", mixed.Status);
        var log = Assert.Single(manifest.Files, file => file.Scenario == "large-log");
        Assert.True(log.Bytes >= 64L * 1024 * 1024);
        Assert.True(log.Lines > 0);
        Assert.Equal(new FileInfo(Path.Combine(fixture.Root, log.RelativePath)).Length, log.Bytes);
        var global = manifest.Scenarios.Single(scenario => scenario.Scenario == "global-budget");
        Assert.Equal(41, global.Files);
        Assert.Equal(200_000, global.Occurrences);
        Assert.Equal(40, global.MatchedFiles);
        Assert.Equal("Limited", global.Status);
    }

    [Theory]
    [InlineData("many-small")]
    [InlineData("large-log")]
    [InlineData("mixed")]
    [InlineData("long-line")]
    [InlineData("long-field")]
    [InlineData("dense-file")]
    [InlineData("global-budget")]
    [InlineData("errors")]
    [InlineData("literal-and")]
    [InlineData("cancel")]
    public async Task ScenariosMeetStableWholeFileAndDiagnosticExpectations(string scenario)
    {
        var report = await BenchmarkRunner.RunAsync(fixture.Root, scenario, repeat: 1);
        var round = Assert.Single(report.Rounds);
        Assert.True(report.Passed, string.Join(",", round.Mismatches));
        Assert.True(report.Integrity.VerifiedBeforeAndAfter);
        Assert.Equal(report.Expected.MatchedFiles, round.PublishedFiles);
        Assert.Equal(report.Expected.Occurrences, round.PublishedOccurrences);
        Assert.Equal(report.Expected.MatchesPerPublishedFile, round.MinimumMatchesPerPublishedFile);
        Assert.Equal(report.Expected.MatchesPerPublishedFile, round.MaximumMatchesPerPublishedFile);
        Assert.InRange(round.IssueDetailsObserved, 0, 1001);
        if (scenario == "errors")
        {
            Assert.Equal(1105, round.ErrorCount);
            Assert.True(round.SuppressedIssueCount > 0);
        }
        if (scenario == "cancel")
        {
            Assert.NotNull(round.Cancellation);
            Assert.True(round.Cancellation.Requested);
            Assert.True(round.Cancellation.TaskEnded);
            Assert.Equal(0, round.Cancellation.ResultsAfterRequest);
            Assert.True(round.Cancellation.RequestToCompletionMilliseconds >= 0);
        }
    }

    [Fact]
    public async Task SearchPreservesAllHashesAndWriteTimesIncludingControlFiles()
    {
        var before = Stamps(fixture.Root);
        await BenchmarkRunner.RunAsync(fixture.Root, "literal-and", repeat: 2);
        Assert.Equal(before, Stamps(fixture.Root));
    }

    [Fact]
    public async Task JsonContainsAggregatesAndExplicitOmissionsButNoCorpusPayload()
    {
        var report = await BenchmarkRunner.RunAsync(fixture.Root, "literal-and", repeat: 2);
        string json = BenchmarkJson.Serialize(report);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("西瓜", json, StringComparison.Ordinal);
        Assert.DoesNotContain("苹果", json, StringComparison.Ordinal);
        Assert.DoesNotContain("both.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("regex-decoy", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        AssertNoPayloadProperties(document.RootElement, fixture.Root);
        var omitted = document.RootElement.GetProperty("omittedFields").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("query", omitted);
        Assert.Contains("body", omitted);
        Assert.Contains("paths", omitted);
        Assert.Equal("first-round", report.Rounds[0].Phase);
        Assert.Equal("repeat", report.Rounds[1].Phase);
        Assert.NotNull(report.RepeatedElapsed);
    }

    [Fact]
    public async Task CliFailureIsStructuredAndDoesNotEchoUnknownInput()
    {
        using var output = new StringWriter();
        int exit = await BenchmarkCli.ExecuteAsync(["run", fixture.Root, "private-query-or-path"], output);
        string json = output.ToString();
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("private-query-or-path", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("unknown-scenario", parsed.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("many-small/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("many-small\\..\\outside.txt")]
    [InlineData("many-small/file.txt:stream")]
    public void ManifestPathsRejectTraversalAbsoluteAndAlternateStreams(string path)
    {
        Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.ResolveOwnedPath(fixture.Root, path));
        var files = fixture.Manifest.Files.ToArray();
        files[0] = files[0] with { RelativePath = path };
        var malicious = fixture.Manifest with { Files = files };
        Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.Verify(fixture.Root, malicious));
    }

    [Fact]
    public void PrepareRejectsOwnedExistingCorpusRatherThanClearingIt()
    {
        var before = Stamps(fixture.Root);
        var error = Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.Prepare(fixture.Root, new PrepareOptions(true)));
        Assert.Equal("root-must-be-new-or-empty", error.Code);
        Assert.Equal(before, Stamps(fixture.Root));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void GenerationSizeIsBounded(int size)
    {
        Assert.Throws<BenchmarkException>(() => new PrepareOptions(LogMiB: size).Validate());
    }

    [Fact]
    public void UnfamiliarNonemptyRootAndTamperedCorpusArePreservedAndRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Benchmarks.Guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sentinel = Path.Combine(directory, "sentinel.txt");
            File.WriteAllText(sentinel, "测试所有者正文");
            var before = Stamps(directory);
            Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.Prepare(directory, new PrepareOptions(true)));
            Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.OpenVerified(directory));
            Assert.Equal(before, Stamps(directory));

            // A marker/manifest copied onto unrelated bytes is not a valid tool corpus.
            File.Copy(Path.Combine(fixture.Root, BenchmarkCorpus.MarkerName), Path.Combine(directory, BenchmarkCorpus.MarkerName));
            File.Copy(Path.Combine(fixture.Root, BenchmarkCorpus.ManifestName), Path.Combine(directory, BenchmarkCorpus.ManifestName));
            Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.OpenVerified(directory));
            Assert.Equal("测试所有者正文", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void HashAndMtimeManifestMismatchesAreRejected()
    {
        var files = fixture.Manifest.Files.ToArray();
        files[0] = files[0] with { Sha256 = new string('0', 64) };
        Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.Verify(fixture.Root, fixture.Manifest with { Files = files }));
        files[0] = fixture.Manifest.Files[0] with { LastWriteTimeUtcTicks = fixture.Manifest.Files[0].LastWriteTimeUtcTicks + 1 };
        Assert.Throws<BenchmarkException>(() => BenchmarkCorpus.Verify(fixture.Root, fixture.Manifest with { Files = files }));
    }

    [Fact]
    public async Task CliRejectsOutputInsideCorpusAndNeverOverwritesExistingOutput()
    {
        using var inside = new StringWriter();
        string forbidden = Path.Combine(fixture.Root, "result.json");
        int exit = await BenchmarkCli.ExecuteAsync(["run", fixture.Root, "literal-and", "--output", forbidden], inside);
        Assert.NotEqual(0, exit);
        Assert.False(File.Exists(forbidden));
        Assert.Contains("output-must-be-outside-corpus", inside.ToString(), StringComparison.Ordinal);
        string existing = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Benchmarks.Output-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(existing, "retain-me");
        try
        {
            using var outside = new StringWriter();
            exit = await BenchmarkCli.ExecuteAsync(["run", fixture.Root, "literal-and", "--output", existing], outside);
            Assert.NotEqual(0, exit);
            Assert.Equal("retain-me", File.ReadAllText(existing));
        }
        finally { File.Delete(existing); }
    }

    [Fact]
    public async Task MalformedManifestProducesOnlyStructuredDiagnosticCode()
    {
        string root = Path.Combine(Path.GetTempPath(), "LocalDocSearch.Benchmarks.Malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, BenchmarkCorpus.ManifestName), "{}");
            using var output = new StringWriter();
            int exit = await BenchmarkCli.ExecuteAsync(["run", root, "many-small"], output);
            Assert.NotEqual(0, exit);
            using var parsed = JsonDocument.Parse(output.ToString());
            Assert.False(parsed.RootElement.GetProperty("passed").GetBoolean());
            AssertNoPayloadProperties(parsed.RootElement, root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] Stamps(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path =>
        {
            using var stream = File.OpenRead(path);
            return Path.GetRelativePath(root, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(stream));
        }).ToArray();

    private static void AssertNoPayloadProperties(JsonElement element, string root)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "query", "terms", "body", "path", "paths", "fullPath", "currentPath", "message" });
                AssertNoPayloadProperties(property.Value, root);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) AssertNoPayloadProperties(child, root);
        else if (element.ValueKind == JsonValueKind.String)
        {
            string value = element.GetString()!;
            Assert.DoesNotContain(root, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("西瓜", value, StringComparison.Ordinal);
            Assert.DoesNotContain("苹果", value, StringComparison.Ordinal);
            Assert.DoesNotContain("both.txt", value, StringComparison.Ordinal);
            Assert.DoesNotContain(".*", value, StringComparison.Ordinal);
        }
    }
}
