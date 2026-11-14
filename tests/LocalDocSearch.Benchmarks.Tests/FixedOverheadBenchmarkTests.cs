using System.Text.Json;
using LocalDocSearch.Benchmarks;
using Xunit;

namespace LocalDocSearch.Benchmarks.Tests;

public sealed class FixedOverheadBenchmarkTests
{
    [Fact]
    public async Task SmallCoordinateCommandReportsExactCallsAndRepeatableFailureOutputs()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await BenchmarkCli.ExecuteAsync(["overhead-coordinates", "--repeat", "2", "--iterations", "127"], output));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("passed").GetBoolean());
        Assert.Equal("overhead-coordinates", root.GetProperty("command").GetString());
        Assert.Equal(127, root.GetProperty("iterations").GetInt32());
        Assert.Equal(2, root.GetProperty("rounds").GetArrayLength());
        var rounds = root.GetProperty("rounds").EnumerateArray().ToArray();
        foreach (var round in rounds)
        {
            Assert.True(round.GetProperty("passed").GetBoolean());
            Assert.Equal(127, round.GetProperty("calls").GetInt64());
            Assert.True(round.GetProperty("totalAllocatedBytesDelta").GetInt64() >= 0);
            Assert.True(round.GetProperty("elapsedMilliseconds").GetDouble() >= 0);
            Assert.Equal(rounds[0].GetProperty("sum").GetInt64(), round.GetProperty("sum").GetInt64());
            Assert.Equal(rounds[0].GetProperty("checksum").GetInt64(), round.GetProperty("checksum").GetInt64());
        }
        foreach (string field in new[] { "extractorsSha256", "coreSha256", "driverSha256", "inputsSha256" })
            Assert.Matches("^[A-F0-9]{64}$", root.GetProperty(field).GetString()!);
        var verification = root.GetProperty("resultVerification").EnumerateArray().ToArray();
        Assert.Equal(root.GetProperty("inputCount").GetInt32(), verification.Length);
        var boundary = verification.Single(item => item.TryGetProperty("address", out var address) && address.GetString() == "XFD1048576");
        Assert.True(boundary.GetProperty("success").GetBoolean());
        Assert.Equal(1048576, boundary.GetProperty("row").GetInt32());
        Assert.Equal(16384, boundary.GetProperty("column").GetInt32());
        var invalid = verification.Single(item => item.TryGetProperty("address", out var address) && address.GetString() == "XFE1");
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Equal(1, invalid.GetProperty("row").GetInt32());
        Assert.Equal(16385, invalid.GetProperty("column").GetInt32());
    }

    [Theory]
    [InlineData("--repeat 0")]
    [InlineData("--repeat 21")]
    [InlineData("--iterations 0")]
    [InlineData("--iterations 20000001")]
    [InlineData("--iterations -1")]
    [InlineData("--iterations nope")]
    [InlineData("--iterations")]
    [InlineData("--repeat 1 --repeat 1")]
    [InlineData("--output")]
    [InlineData("--unknown")]
    public async Task InvalidCoordinateOptionsReturnOnlyFailureJson(string options)
    {
        using var output = new StringWriter();
        Assert.Equal(1, await BenchmarkCli.ExecuteAsync(["overhead-coordinates", .. options.Split(' ')], output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task CoordinateOutputCreatesNewFileAndPreservesExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "FixedOverhead-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var output = new StringWriter();
            string[] args = ["overhead-coordinates", "--iterations", "1", "--repeat", "1", "--output", path];
            Assert.Equal(0, await BenchmarkCli.ExecuteAsync(args, output));
            string saved = File.ReadAllText(path);
            Assert.Equal(output.ToString().Trim(), saved.Trim());
            using var second = new StringWriter();
            Assert.Equal(1, await BenchmarkCli.ExecuteAsync(args, second));
            Assert.Equal(saved, File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
