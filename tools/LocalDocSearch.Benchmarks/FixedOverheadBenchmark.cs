using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;

namespace LocalDocSearch.Benchmarks;

public sealed record CoordinateObservation(string? Address, bool Success, int Row, int Column);
public sealed record CoordinateRound(int Round, long Calls, long SuccessCount, long Sum, long Checksum,
    double ElapsedMilliseconds, double CpuMilliseconds, long TotalAllocatedBytesDelta,
    long ProcessWorkingSetBytesAfter, long ProcessPeakWorkingSetBytesLifetime, bool Passed);
public sealed record CoordinateReport(string Command, int Iterations, int Repeat, int InputCount,
    MachineInfo Machine, string ExtractorsSha256, string CoreSha256, string DriverSha256,
    string InputsSha256, IReadOnlyList<CoordinateObservation> ResultVerification,
    IReadOnlyList<CoordinateRound> Rounds, bool Passed,
    string TimingSemantics = "delegate binding, input creation, SHA256, verification and warmup excluded; timed loop includes delegate call, counters and checksum; iterations counts exact calls per round",
    string PeakSemantics = "process lifetime cumulative peak, not a round delta; allocations are process-wide GC delta",
    string CacheSemantics = "OS cache not cleared; each command is intended to run in a fresh process");

/// <summary>Fixed original inputs; compatible with both private rc9 and internal candidate coordinate methods.</summary>
public static class FixedOverheadBenchmark
{
    public const int DefaultIterations = 500_000, MinimumIterations = 1, MaximumIterations = 20_000_000;
    private delegate bool Coordinates(string? address, out int row, out int column);

    public static CoordinateReport RunCoordinates(int repeat = 3, int iterations = DefaultIterations)
    {
        if (repeat is < 1 or > 20) throw new BenchmarkException("invalid-repeat");
        if (iterations is < MinimumIterations or > MaximumIterations) throw new BenchmarkException("invalid-iterations");
        // Locate the method on the actually loaded extractor assembly; never Invoke in the loop.
        var assembly = typeof(ExcelTextExtractor).Assembly;
        var type = assembly.GetType("LocalDocSearch.Extractors.ExcelTextExtractor", throwOnError: true)!;
        var method = type.GetMethod("TryCoordinates", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: [typeof(string), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()], modifiers: null);
        if (method is null || method.ReturnType != typeof(bool) || method.IsGenericMethod ||
            !method.GetParameters()[1].IsOut || !method.GetParameters()[2].IsOut)
            throw new BenchmarkException("coordinate-method-signature-mismatch");
        var coordinates = method.CreateDelegate<Coordinates>();
        string?[] inputs = [null, "", "A1", "a1", "z9", "AA10", "xFd1048576", "XFD1048576", "A1048576",
            "XFD1", "A1\n", "aB234\n", "XFD1048576\n", "A0", "A01", "A1048577", "XFE1", "ZZZ9999999",
            "AAAA1", "A2147483648", "A9999999", "A١", "A1٢", "A1１", "Ａ1", "Ä1", " A1", "A1 ",
            "$A$1", "A-1", "A+1", "1A", "A1\r\n", "A1\n\n", "A1\nB2", "A\n1", "ZZZ1", "IV65536",
            "a1234567", "ABC999999", "A0000001", "A11111111"];
        var verification = inputs.Select(address =>
        {
            bool success = coordinates(address, out int row, out int column);
            return new CoordinateObservation(address, success, row, column);
        }).ToArray();
        var expected = Accumulate(iterations, inputs.Length, index => verification[index]);
        _ = Loop(coordinates, inputs, Math.Min(iterations, 20_000));
        using var process = Process.GetCurrentProcess();
        var rounds = new List<CoordinateRound>();
        for (int number = 1; number <= repeat; number++)
        {
            var clock = new Stopwatch();
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(true);
            clock.Start();
            var actual = Loop(coordinates, inputs, iterations);
            clock.Stop();
            long allocationDelta = GC.GetTotalAllocatedBytes(true) - allocated;
            TimeSpan cpuAfter = process.TotalProcessorTime;
            process.Refresh();
            rounds.Add(new(number, iterations, actual.Success, actual.Sum, actual.Checksum,
                clock.Elapsed.TotalMilliseconds, (cpuAfter - cpu).TotalMilliseconds, allocationDelta,
                process.WorkingSet64, process.PeakWorkingSet64, actual == expected));
        }
        var machine = new MachineInfo(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Stopwatch.Frequency,
            System.Runtime.GCSettings.IsServerGC);
        return new("overhead-coordinates", iterations, repeat, inputs.Length, machine,
            Hash(assembly.Location), Hash(typeof(TextSearchEngine).Assembly.Location), Hash(typeof(FixedOverheadBenchmark).Assembly.Location),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs))),
            Array.AsReadOnly(verification), rounds.AsReadOnly(), rounds.All(round => round.Passed));
    }

    private static (long Success, long Sum, long Checksum) Loop(Coordinates coordinates, string?[] inputs, int iterations)
    {
        long successCount = 0, sum = 0, checksum = 1469598103934665603;
        for (int i = 0, index = 0; i < iterations; i++)
        {
            bool success = coordinates(inputs[index], out int row, out int column);
            successCount += success ? 1 : 0;
            sum += (long)row + column;
            unchecked { checksum = (checksum ^ row) * 1099511628211; checksum = (checksum ^ column) * 1099511628211;
                checksum = (checksum ^ (success ? 1 : 0)) * 1099511628211; }
            if (++index == inputs.Length) index = 0;
        }
        return (successCount, sum, checksum);
    }

    private static (long Success, long Sum, long Checksum) Accumulate(int iterations, int inputCount, Func<int, CoordinateObservation> observation)
    {
        long successCount = 0, sum = 0, checksum = 1469598103934665603;
        for (int i = 0, index = 0; i < iterations; i++)
        {
            var value = observation(index);
            successCount += value.Success ? 1 : 0;
            sum += (long)value.Row + value.Column;
            unchecked { checksum = (checksum ^ value.Row) * 1099511628211; checksum = (checksum ^ value.Column) * 1099511628211;
                checksum = (checksum ^ (value.Success ? 1 : 0)) * 1099511628211; }
            if (++index == inputCount) index = 0;
        }
        return (successCount, sum, checksum);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
