using System.Text;

namespace LocalDocSearch.Benchmarks;

public static class Program
{
    public static Task<int> Main(string[] args) => BenchmarkCli.ExecuteAsync(args, Console.Out);
}

public static class BenchmarkCli
{
    public static async Task<int> ExecuteAsync(string[] args, TextWriter output)
    {
        string? scenario = null;
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "help")
            {
                await output.WriteLineAsync(BenchmarkJson.Serialize(new
                {
                    schemaVersion = 1, tool = BenchmarkCorpus.Generator,
                    commands = new[] { "prepare <new-or-empty-root> [--quick] [--log-mib 64..256]",
                        "run <owned-root> <scenario> [--repeat 1..20] [--workers 1|2|4] [--output new-json-file]",
                        "control <owned-root> <many-small|mixed> [--workers 1|2|4] [--output new-json-file]" },
                    scenarios = BenchmarkCorpus.ScenarioNames, omittedFields = BenchmarkRunner.OmittedFields
                }));
                return 0;
            }
            if (args.Length < 2) throw new BenchmarkException("invalid-arguments");
            string command = args[0], root = args[1];
            if (command == "prepare")
            {
                bool quick = false;
                int logMiB = 64;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 2; index < args.Length; index++)
                {
                    string option = args[index];
                    if (!seen.Add(option)) throw new BenchmarkException("duplicate-option");
                    if (option == "--quick") quick = true;
                    else if (option == "--log-mib") logMiB = IntegerOption(args, ref index);
                    else throw new BenchmarkException("unknown-option");
                }
                var manifest = BenchmarkCorpus.Prepare(root, new PrepareOptions(quick, logMiB));
                await output.WriteLineAsync(BenchmarkJson.Serialize(new
                {
                    schemaVersion = 1, tool = BenchmarkCorpus.Generator, command, passed = true,
                    manifest.Profile, manifest.LogMiB, generatedFiles = manifest.Files.Count,
                    generatedBytes = manifest.Files.Sum(file => file.Bytes), scenarios = manifest.Scenarios,
                    integrityVerified = true, omittedFields = BenchmarkRunner.OmittedFields
                }));
                return 0;
            }
            if (command is not ("run" or "control") || args.Length < 3) throw new BenchmarkException("invalid-arguments");
            if (!BenchmarkCorpus.ScenarioNames.Contains(args[2], StringComparer.Ordinal))
                throw new BenchmarkException("unknown-scenario");
            scenario = args[2];
            int repeat = 3, workers = 1;
            string? outputPath = null;
            var runSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 3; index < args.Length; index++)
            {
                string option = args[index];
                if (!runSeen.Add(option)) throw new BenchmarkException("duplicate-option");
                if (option == "--repeat" && command == "run") repeat = IntegerOption(args, ref index);
                else if (option == "--workers")
                {
                    if (++index >= args.Length) throw new BenchmarkException("invalid-workers");
                    workers = args[index] switch
                    {
                        "1" => 1, "2" => 2, "4" => 4,
                        _ => throw new BenchmarkException("invalid-workers")
                    };
                }
                else if (option == "--output")
                {
                    if (++index >= args.Length) throw new BenchmarkException("missing-option-value");
                    outputPath = ValidateOutputPath(root, args[index]);
                }
                else throw new BenchmarkException("unknown-option");
            }
            BenchmarkRunner.ValidateWorkers(workers);
            string json;
            bool passed;
            if (command == "control")
            {
                var report = await ControlBenchmarkRunner.RunAsync(root, scenario, workers).ConfigureAwait(false);
                json = BenchmarkJson.Serialize(report);
                passed = report.Passed;
            }
            else
            {
                var report = await BenchmarkRunner.RunAsync(root, scenario, repeat, workers).ConfigureAwait(false);
                json = BenchmarkJson.Serialize(report);
                passed = report.Passed;
            }
            if (outputPath is not null)
            {
                BenchmarkCorpus.AssertOrdinaryAncestors(outputPath);
                using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(json).ConfigureAwait(false);
            }
            await output.WriteLineAsync(json);
            return passed ? 0 : 2;
        }
        catch (Exception exception)
        {
            string code = exception is BenchmarkException benchmark ? benchmark.Code : exception switch
            {
                IOException => "io-error", UnauthorizedAccessException => "access-error", _ => "validation-or-execution-error"
            };
            await output.WriteLineAsync(BenchmarkJson.Serialize(new
            {
                schemaVersion = 1, tool = BenchmarkCorpus.Generator, scenario, passed = false,
                errorCode = code, omittedFields = BenchmarkRunner.OmittedFields
            }));
            return 1;
        }
    }

    private static int IntegerOption(string[] args, ref int index)
    {
        if (++index >= args.Length || !int.TryParse(args[index], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int value))
            throw new BenchmarkException("invalid-integer-option");
        return value;
    }

    private static string ValidateOutputPath(string root, string requestedPath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(requestedPath);
        if (path.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BenchmarkException("output-must-be-outside-corpus");
        BenchmarkCorpus.AssertOrdinaryAncestors(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new BenchmarkException("output-must-be-new");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new BenchmarkException("output-parent-must-exist");
        return path;
    }
}
