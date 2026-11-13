using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalDocSearch.Fixtures;

namespace LocalDocSearch.Benchmarks;

/// <summary>Owns only a newly created corpus. There is deliberately no delete or repair operation.</summary>
public static class BenchmarkCorpus
{
    public const string MarkerName = ".localdocsearch-benchmark-root";
    public const string ManifestName = "benchmark-manifest.json";
    public const string Generator = "LocalDocSearch.Benchmarks/original-v1";
    public const int SegmentLimit = 1_048_576;
    private const int Version = 1;
    private const int MaximumFiles = 10_000;
    private const long MaximumCorpusBytes = 512L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] CorpusScenarios =
        ["many-small", "large-log", "mixed", "long-line", "long-field", "dense-file", "global-budget", "errors", "literal-and"];
    private static readonly string[] Names = [.. CorpusScenarios, "cancel"];

    public static IReadOnlyList<string> ScenarioNames => Names;

    public static CorpusManifest Prepare(string root, PrepareOptions options)
    {
        options.Validate();
        root = NormalizeRoot(root);
        AssertOrdinaryAncestors(root);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
            throw new BenchmarkException("root-must-be-new-or-empty");
        Directory.CreateDirectory(root);
        AssertOrdinaryAncestors(root);
        var corpusId = Guid.NewGuid().ToString("N");
        WriteNew(Path.Combine(root, MarkerName), Utf8.GetBytes(Generator + "\n" + corpusId + "\n"));
        var files = new List<CorpusFile>();
        var scenarios = new List<ScenarioExpectation>();

        string DirectoryFor(string name)
        {
            string directory = Path.Combine(root, name);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new BenchmarkException("generation-collision");
            Directory.CreateDirectory(directory);
            AssertOrdinaryAncestors(directory);
            return directory;
        }
        void Register(string scenario, string path, long? lines = null)
        {
            var info = new FileInfo(path);
            AssertOrdinaryAncestors(path);
            files.Add(new CorpusFile(Path.GetRelativePath(root, path).Replace('\\', '/'), scenario,
                info.Length, info.LastWriteTimeUtc.Ticks, Hash(path), lines));
        }
        void Text(string scenario, string directory, string name, string content, long? lines = null)
        {
            string path = Path.Combine(directory, name);
            WriteNew(path, Utf8.GetBytes(content));
            Register(scenario, path, lines);
        }
        void Describe(string name, int matchedFiles, int occurrences, string status = "Completed",
            long errors = 0, long warnings = 0, int perFile = 2, string? corpus = null, bool cancel = false)
        {
            string source = corpus ?? name;
            var selected = files.Where(file => file.Scenario == source).ToArray();
            long? lines = selected.All(file => file.Lines.HasValue) ? selected.Sum(file => file.Lines!.Value) : null;
            scenarios.Add(new ScenarioExpectation(name, source, selected.Length, selected.Sum(file => file.Bytes),
                lines, matchedFiles, occurrences, status, errors, warnings, perFile, cancel));
        }

        var small = DirectoryFor("many-small");
        int smallCount = options.Quick ? 200 : 5000;
        for (int index = 0; index < smallCount; index++)
            Text("many-small", small, $"original-{index:D5}.txt", "原创入库：西瓜\n原创领用：苹果；字面量：.*\n", 2);
        Describe("many-small", smallCount, smallCount * 2);

        var logDirectory = DirectoryFor("large-log");
        string logPath = Path.Combine(logDirectory, "original-large.log");
        const string regularLine = "原创短行日志 0123456789 abcdefghijklmnopqrstuvwxyz 0123456789 abcdefghijklmnopqrstuvwxyz 0123456789";
        const string matchedLine = "原创短行日志 西瓜 0123456789 abcdefghijklmnopqrstuvwxyz 0123456789 abcdefghijklmnopqrstuvwxyz 0123456789";
        long bytes = 0, lineCount = 0;
        int logMatches = 0;
        using (var stream = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8, 64 * 1024) { NewLine = "\n" })
        {
            long target = options.LogMiB * 1024L * 1024;
            while (bytes < target)
            {
                bool match = lineCount % 8192 == 0;
                string line = match ? matchedLine : regularLine;
                writer.WriteLine(line);
                bytes += Utf8.GetByteCount(line) + 1;
                lineCount++;
                if (match) logMatches++;
            }
        }
        Register("large-log", logPath, lineCount);
        Describe("large-log", 1, logMatches, perFile: logMatches);

        var mixed = DirectoryFor("mixed");
        string baseDirectory = Path.Combine(mixed, "set-00");
        Directory.CreateDirectory(baseDirectory);
        SampleDocuments.CreateAll(baseDirectory);
        string[] baseFiles = Directory.GetFiles(baseDirectory);
        if (baseFiles.Length != 8) throw new BenchmarkException("original-fixture-shape-changed");
        foreach (string path in baseFiles) Register("mixed", path);
        for (int set = 1; set < 25; set++)
        {
            string destination = Path.Combine(mixed, $"set-{set:D2}");
            Directory.CreateDirectory(destination);
            foreach (string source in baseFiles)
            {
                string path = Path.Combine(destination, Path.GetFileName(source));
                File.Copy(source, path, overwrite: false);
                Register("mixed", path);
            }
        }
        Describe("mixed", 200, 400, "Partial", warnings: 100);

        string oversized = "西瓜" + new string('x', SegmentLimit - 1); // exactly limit + 1 UTF-16 characters
        var longLine = DirectoryFor("long-line");
        Text("long-line", longLine, "oversized.txt", "西瓜\n" + oversized + "\n", 2);
        Text("long-line", longLine, "survivor.txt", "西瓜\n西瓜\n", 2);
        Describe("long-line", 1, 2, "Partial", errors: 1);
        var longField = DirectoryFor("long-field");
        Text("long-field", longField, "oversized.csv", "西瓜,\"" + oversized + "\"\n", 1);
        Text("long-field", longField, "survivor.csv", "西瓜,西瓜\n", 1);
        Describe("long-field", 1, 2, "Partial", errors: 1);

        var dense = DirectoryFor("dense-file");
        Text("dense-file", dense, "too-dense.txt", string.Concat(Enumerable.Repeat("西瓜\n", 50_001)), 50_001);
        Text("dense-file", dense, "survivor.txt", "西瓜\n西瓜\n", 2);
        Describe("dense-file", 1, 2, "Partial", errors: 1);

        var budget = DirectoryFor("global-budget");
        string budgetContent = string.Concat(Enumerable.Repeat("西瓜\n", 5000));
        for (int index = 0; index < 41; index++)
            Text("global-budget", budget, $"budget-{index:D2}.txt", budgetContent, 5000);
        Describe("global-budget", 40, 200_000, "Limited", errors: 1, perFile: 5000);

        var errorsDirectory = DirectoryFor("errors");
        int errorCount = options.Quick ? 1105 : 1200;
        for (int index = 0; index < errorCount; index++)
        {
            string path = Path.Combine(errorsDirectory, $"invalid-{index:D4}.txt");
            WriteNew(path, [0xc3, 0x28, 0xff]); // no BOM; deterministically invalid strict UTF-8
            Register("errors", path);
        }
        Text("errors", errorsDirectory, "survivor.txt", "西瓜\n西瓜\n", 2);
        Describe("errors", 1, 2, "Partial", errors: errorCount);

        var literal = DirectoryFor("literal-and");
        Text("literal-and", literal, "both.txt", "西瓜\n.*\n", 2);
        Text("literal-and", literal, "only-fruit.txt", "西瓜\n", 1);
        Text("literal-and", literal, "only-literal.txt", ".*\n", 1);
        Text("literal-and", literal, "regex-decoy.txt", "abXYZ\n", 1);
        Describe("literal-and", 1, 2);
        Describe("cancel", 1, 2, "Cancelled", corpus: "many-small", cancel: true);

        var manifest = new CorpusManifest(Version, Generator, corpusId, options.Quick ? "quick" : "standard",
            options.LogMiB, files.AsReadOnly(), scenarios.AsReadOnly());
        WriteNew(Path.Combine(root, ManifestName), Utf8.GetBytes(BenchmarkJson.Serialize(manifest)));
        Verify(root, manifest);
        return manifest;
    }

    public static CorpusManifest OpenVerified(string root)
    {
        root = NormalizeRoot(root);
        AssertOrdinaryAncestors(root);
        string manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 16 * 1024 * 1024)
            throw new BenchmarkException("missing-or-invalid-manifest");
        AssertOrdinaryAncestors(manifestPath);
        CorpusManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(manifestPath, Utf8), BenchmarkJson.Options)
                ?? throw new BenchmarkException("invalid-manifest");
        }
        catch (JsonException) { throw new BenchmarkException("invalid-manifest"); }
        Verify(root, manifest);
        return manifest;
    }

    public static void Verify(string root, CorpusManifest manifest)
    {
        root = NormalizeRoot(root);
        AssertOrdinaryAncestors(root);
        ValidateManifest(manifest);
        string marker = Path.Combine(root, MarkerName);
        AssertOrdinaryAncestors(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 256 ||
            File.ReadAllText(marker, Utf8) != Generator + "\n" + manifest.CorpusId + "\n")
            throw new BenchmarkException("invalid-root-marker");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MarkerName, ManifestName };
        foreach (var file in manifest.Files)
        {
            string path = ResolveOwnedPath(root, file.RelativePath);
            if (!expected.Add(file.RelativePath)) throw new BenchmarkException("duplicate-manifest-path");
            AssertOrdinaryAncestors(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Bytes || info.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtcTicks ||
                !string.Equals(Hash(path), file.Sha256, StringComparison.Ordinal))
                throw new BenchmarkException("corpus-integrity-mismatch");
        }
        var expectedDirectories = manifest.Files.SelectMany(file => AncestorDirectories(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = EnumerateOrdinaryFiles(root, expectedDirectories).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected)) throw new BenchmarkException("unowned-corpus-entry");
    }

    public static string ScenarioRoot(string root, ScenarioExpectation scenario) =>
        ResolveOwnedPath(NormalizeRoot(root), scenario.CorpusScenario);

    public static string ResolveOwnedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':') ||
            Path.IsPathRooted(relativePath) || relativePath.Split('/').Any(part => string.IsNullOrWhiteSpace(part) ||
                part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new BenchmarkException("invalid-manifest-path");
        root = NormalizeRoot(root);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BenchmarkException("invalid-manifest-path");
        return path;
    }

    public static void AssertOrdinaryAncestors(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new BenchmarkException("linked-path");
        for (DirectoryInfo? directory = new(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!);
            directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BenchmarkException("linked-path");
    }

    private static void ValidateManifest(CorpusManifest manifest)
    {
        if (manifest.Version != Version || manifest.Generator != Generator ||
            !Guid.TryParseExact(manifest.CorpusId, "N", out _) || manifest.Profile is not ("quick" or "standard") ||
            manifest.LogMiB is < 64 or > PrepareOptions.MaximumLogMiB ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles ||
            manifest.Scenarios is null || manifest.Scenarios.Count != Names.Length)
            throw new BenchmarkException("invalid-manifest");
        if (manifest.Files.Any(file => file is null || file.Bytes < 0 || file.Bytes > MaximumCorpusBytes ||
            file.LastWriteTimeUtcTicks < 0 || file.Sha256 is null || file.Sha256.Length != 64 ||
            file.Sha256.Any(character => !char.IsAsciiHexDigit(character)) || !CorpusScenarios.Contains(file.Scenario) ||
            file.RelativePath is null || !file.RelativePath.StartsWith(file.Scenario + "/", StringComparison.Ordinal)) ||
            manifest.Files.Sum(file => file.Bytes) > MaximumCorpusBytes)
            throw new BenchmarkException("invalid-manifest");
        if (!manifest.Scenarios.Select(scenario => scenario.Scenario).ToHashSet(StringComparer.Ordinal).SetEquals(Names))
            throw new BenchmarkException("invalid-manifest");
        foreach (var scenario in manifest.Scenarios)
        {
            if (scenario.CorpusScenario != (scenario.Scenario == "cancel" ? "many-small" : scenario.Scenario) ||
                scenario.Cancellation != (scenario.Scenario == "cancel") || scenario.MatchesPerPublishedFile <= 0)
                throw new BenchmarkException("invalid-manifest");
            var selected = manifest.Files.Where(file => file.Scenario == scenario.CorpusScenario).ToArray();
            if (selected.Length != scenario.Files || selected.Sum(file => file.Bytes) != scenario.Bytes)
                throw new BenchmarkException("invalid-manifest");
        }
    }

    private static IEnumerable<string> AncestorDirectories(string relativePath)
    {
        int separator = relativePath.IndexOf('/');
        while (separator >= 0)
        {
            yield return relativePath[..separator];
            separator = relativePath.IndexOf('/', separator + 1);
        }
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string root, HashSet<string> expectedDirectories)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        int fileCount = 0, directoryCount = 0;
        while (pending.TryPop(out string? directory))
        {
            if (++directoryCount > MaximumFiles) throw new BenchmarkException("unowned-corpus-entry");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new BenchmarkException("linked-path");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!expectedDirectories.Contains(relative)) throw new BenchmarkException("unowned-corpus-entry");
                    pending.Push(path);
                }
                else
                {
                    if (++fileCount > MaximumFiles + 2) throw new BenchmarkException("unowned-corpus-entry");
                    yield return path;
                }
            }
        }
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(fullPath) == fullPath) throw new BenchmarkException("filesystem-root-not-allowed");
        return fullPath;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        AssertOrdinaryAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
