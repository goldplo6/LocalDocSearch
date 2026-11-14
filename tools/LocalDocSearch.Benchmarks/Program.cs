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
                        "control <owned-root> <many-small|mixed> [--workers 1|2|4] [--output new-json-file]",
                        "literal-prepare <new-root> [--quick]",
                        "literal-run <owned-root> <scenario> [--repeat 1..20] [--output new-json-file]",
                        "overhead-coordinates [--repeat 1..20] [--iterations 1..20000000] [--output new-json-file]" },
                    scenarios = BenchmarkCorpus.ScenarioNames, omittedFields = BenchmarkRunner.OmittedFields
                }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "overhead-coordinates")
            {
                int coordinateRepeat = 3, iterations = FixedOverheadBenchmark.DefaultIterations;
                string? coordinateOutput = null;
                var coordinateSeen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 1; index < args.Length; index++)
                {
                    string option = args[index];
                    if (!coordinateSeen.Add(option)) throw new BenchmarkException("duplicate-option");
                    if (option == "--repeat") coordinateRepeat = IntegerOption(args, ref index);
                    else if (option == "--iterations") iterations = IntegerOption(args, ref index);
                    else if (option == "--output" && ++index < args.Length)
                    {
                        coordinateOutput = Path.GetFullPath(args[index]);
                        BenchmarkCorpus.AssertOrdinaryAncestors(coordinateOutput);
                        if (File.Exists(coordinateOutput) || Directory.Exists(coordinateOutput))
                            throw new BenchmarkException("output-must-be-new");
                        if (!Directory.Exists(Path.GetDirectoryName(coordinateOutput)))
                            throw new BenchmarkException("output-parent-must-exist");
                    }
                    else throw new BenchmarkException("unknown-or-incomplete-option");
                }
                var report = FixedOverheadBenchmark.RunCoordinates(coordinateRepeat, iterations);
                string coordinateJson = BenchmarkJson.Serialize(report);
                if (coordinateOutput is not null)
                {
                    BenchmarkCorpus.AssertOrdinaryAncestors(coordinateOutput);
                    using var stream = new FileStream(coordinateOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(coordinateJson).ConfigureAwait(false);
                }
                await output.WriteLineAsync(coordinateJson);
                return report.Passed ? 0 : 2;
            }
            if (args.Length < 2) throw new BenchmarkException("invalid-arguments");
            string command = args[0], root = args[1];
            if (command is "literal-prepare" or "literal-run")
            {
                if (command == "literal-prepare")
                {
                    if (args.Length > 3 || args.Length == 3 && args[2] != "--quick")
                        throw new BenchmarkException("invalid-arguments");
                    var manifest = LiteralPrefilterBenchmark.Prepare(root, args.Length == 3);
                    await output.WriteLineAsync(BenchmarkJson.Serialize(new { passed = true, manifest,
                        scenarios = LiteralPrefilterBenchmark.Scenarios }));
                    return 0;
                }
                if (args.Length < 3) throw new BenchmarkException("invalid-arguments");
                scenario = args[2];
                LiteralPrefilterBenchmark.Expected(scenario, true);
                int literalRepeat = 3;
                string? literalOutput = null;
                var literalSeen = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 3; index < args.Length; index++)
                {
                    if (!literalSeen.Add(args[index])) throw new BenchmarkException("duplicate-option");
                    if (args[index] == "--repeat") literalRepeat = IntegerOption(args, ref index);
                    else if (args[index] == "--output" && ++index < args.Length)
                        literalOutput = ValidateOutputPath(root, args[index]);
                    else throw new BenchmarkException("unknown-or-incomplete-option");
                }
                var literalReport = await LiteralPrefilterBenchmark.RunAsync(root, scenario, literalRepeat).ConfigureAwait(false);
                string literalJson = BenchmarkJson.Serialize(literalReport);
                if (literalOutput is not null)
                {
                    BenchmarkCorpus.AssertOrdinaryAncestors(literalOutput);
                    using var stream = new FileStream(literalOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(literalJson).ConfigureAwait(false);
                }
                await output.WriteLineAsync(literalJson);
                return literalReport.Passed ? 0 : 2;
            }
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
