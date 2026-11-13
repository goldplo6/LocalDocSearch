using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Core;

namespace LocalDocSearch.Benchmarks;

/// <summary>Only digest strings leave the process; each actual MatchItem field participates.</summary>
public sealed class ResultDigest(string root) : IDisposable
{
    private readonly List<(string Relative, byte[] Digest)> _files = [];
    private readonly object _sync = new();

    public void Add(FileResult result)
    {
        string relative = Path.GetRelativePath(root, result.FullPath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new BenchmarkException("result-outside-scenario-root");
        using var sha = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            // Serialize the actual result, substituting only its absolute path. This includes all
            // MatchItem / SourceLocator fields, match order, timestamps, extension and warnings.
            JsonSerializer.Serialize(stream, new { relative, result.Extension, result.LastWriteTimeUtc,
                result.Matches, result.ExtractionWarnings });
            stream.FlushFinalBlock();
        }
        lock (_sync) _files.Add((relative, sha.Hash!));
    }

    public string OrderSha256() => Aggregate(ordered: false);
    public string ContentSha256() => Aggregate(ordered: true);

    private string Aggregate(bool ordered)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        lock (_sync)
        {
            IEnumerable<(string Relative, byte[] Digest)> files = ordered
                ? _files.OrderBy(file => file.Relative, StringComparer.Ordinal) : _files;
            foreach (var file in files) hash.AppendData(file.Digest); // fixed-width 32-byte frames
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose() { lock (_sync) _files.Clear(); }
}
