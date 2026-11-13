using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalDocSearch.Benchmarks;

public sealed record PrepareOptions(bool Quick = false, int LogMiB = 64)
{
    public const int MaximumLogMiB = 256;
    public void Validate()
    {
        if (LogMiB is < 64 or > MaximumLogMiB)
            throw new BenchmarkException("invalid-size");
    }
}

public sealed record CorpusFile(string RelativePath, string Scenario, long Bytes, long LastWriteTimeUtcTicks,
    string Sha256, long? Lines = null);

public sealed record ScenarioExpectation(string Scenario, string CorpusScenario, int Files, long Bytes,
    long? Lines, int MatchedFiles, int Occurrences, string Status, long Errors, long Warnings,
    int MatchesPerPublishedFile, bool Cancellation = false);

public sealed record CorpusManifest(int Version, string Generator, string CorpusId, string Profile, int LogMiB,
    IReadOnlyList<CorpusFile> Files, IReadOnlyList<ScenarioExpectation> Scenarios);

public sealed class BenchmarkException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
