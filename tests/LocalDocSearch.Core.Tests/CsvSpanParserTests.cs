using System.Reflection;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class CsvSpanParserTests
{
    // Reuse the independently frozen character-at-a-time algorithm without editing its fixture.
    private static readonly MethodInfo ReferenceParser = typeof(CsvBlockParserTests).GetMethod(
        "ReferenceExtractAsync", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Frozen CSV reference parser was not found.");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(65536)]
    public async Task OrdinarySpansAndStateTransitionsMatchFrozenParser(int length)
    {
        using var directory = new SearchTestDirectory();
        var plain = new string('x', length);
        var whitespace = new string(' ', length);
        var inputs = new[]
        {
            plain, whitespace, plain + ",,tail,", plain + "\r\nnext\rthird\n",
            plain + "西😀𠮷,tail", "prefix," + plain + "😀西,",
            plain + "\r\r\nnext", plain + "\r", plain + "\r\n",
            "\"quoted\"," + plain + ",\"escaped\"\"quote\"",
            plain + ",\"line one\r\nline two\"," + whitespace,
            "prefix," + plain + "\"invalid", "prefix,\"quoted\"" + plain + "invalid",
            "prefix," + plain + ",\"unterminated"
        };
        foreach (var text in inputs)
        {
            var path = await directory.WriteAsync("ordinary.csv", text);
            AssertEquivalent(await CaptureReferenceAsync(path), await CaptureActualAsync(path));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(32768)]
    public async Task FirstOverBudgetCharacterPreservesPrefixAndFailure(int limit)
    {
        using var directory = new SearchTestDirectory();
        foreach (var suffix in new[] { ",tail", "\r\ntail", "\"invalid", "" })
        {
            foreach (var length in new[] { limit, limit + 1 })
            {
                var path = await directory.WriteAsync("budget.csv", "," + new string('x', length) + suffix);
                var actual = await CaptureActualAsync(path, limit);
                AssertEquivalent(await CaptureReferenceAsync(path, limit), actual);
                if (length > limit)
                {
                    Assert.Equal("", Assert.Single(actual.Segments).Text);
                    Assert.Equal(typeof(SearchResourceLimitException).FullName, actual.Failure?.Type);
                    Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), actual.Failure?.LimitName);
                    Assert.Equal(limit, actual.Failure?.Limit);
                }
            }
        }
    }

    [Theory]
    [InlineData(16383)]
    [InlineData(32767)]
    [InlineData(49151)]
    public async Task CrAtDecoderBoundaryDoesNotYieldUntilRawLookaheadSucceeds(int crPosition)
    {
        using var directory = new SearchTestDirectory();
        var prefix = Encoding.UTF8.GetBytes("ok," + new string('x', crPosition - 3) + "\r");
        var path = await directory.WriteBytesAsync("cr-decoder.csv", [.. prefix, 0xc3, 0x28]);
        var actual = await CaptureActualAsync(path);
        Assert.Equal("ok", Assert.Single(actual.Segments).Text);
        Assert.Equal(typeof(DecoderFallbackException).FullName, actual.Failure?.Type);
        AssertEquivalent(await CaptureReferenceAsync(path), actual);
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public async Task PendingRawCharacterAndPhysicalLineRemainExact(int length)
    {
        using var directory = new SearchTestDirectory();
        var plain = new string('x', length);
        var path = await directory.WriteAsync("pending.csv", plain + "\r" + plain + ",\"a\r\nb\",,\r\nend,");
        var actual = await CaptureActualAsync(path);
        Assert.Null(actual.Failure);
        Assert.Equal<long>(new long[] { 1, 2, 2, 3, 3, 4, 4 },
            actual.Segments.Select(segment => segment.Source.Line).ToArray());
        AssertEquivalent(await CaptureReferenceAsync(path), actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(65536)]
    public async Task CancellationBetweenYieldsPrecedesNextSpanBudgetOrFormatError(int length)
    {
        using var directory = new SearchTestDirectory();
        var text = "first," + new string('x', length) + "\"invalid,tail";
        var path = await directory.WriteAsync("cancel-span.csv", text);
        using var actualCancellation = new CancellationTokenSource();
        var actual = await CaptureAsync(new CsvTextExtractor().ExtractAsync(path,
            Context(5), actualCancellation.Token), actualCancellation);
        using var referenceCancellation = new CancellationTokenSource();
        var reference = await CaptureReferenceAsync(path, 5, referenceCancellation);
        Assert.Equal("first", Assert.Single(actual.Segments).Text);
        Assert.Contains(actual.Failure?.Type, new[]
        {
            typeof(OperationCanceledException).FullName, typeof(TaskCanceledException).FullName
        });
        AssertEquivalent(reference, actual);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task MixedSmallAndMultiBlockRecordsMatchFrozenParserAtEveryBudget()
    {
        using var directory = new SearchTestDirectory();
        var random = new Random(0x5350414e);
        for (var sample = 0; sample < 24; sample++)
        {
            var text = new StringBuilder();
            for (var record = 0; record < 4; record++)
            {
                text.Append(new string('x', random.Next(0, 20000))).Append(',');
                text.Append("\"quoted\"\"text\r\n").Append(new string(' ', random.Next(0, 25))).Append("\",");
                text.Append(record % 2 == 0 ? "\r\n" : "\r");
            }
            if (sample % 3 == 0) text.Append("bad\"quote");
            var path = await directory.WriteAsync("mixed.csv", text.ToString());
            foreach (var limit in new[] { 1, 31, 8191, 8192, 8193, 100000 })
                AssertEquivalent(await CaptureReferenceAsync(path, limit), await CaptureActualAsync(path, limit));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(65536)]
    public async Task QuotedOrdinarySpansPreserveCommasEscapesAndPhysicalLines(int length)
    {
        using var directory = new SearchTestDirectory();
        var run = new string('x', length);
        foreach (var text in new[]
        {
            "first,\"" + run + "\"",
            "first,\"" + run + ",literal,commas\",tail",
            "first,\"" + run + "\"\"escaped\"\"quote\",tail",
            "first,\"" + run + "\r\nnext\rthird\nfourth\",tail",
            "first,\"" + run + "西😀𠮷\",tail",
            "first,\"" + run + "\"invalid,tail",
            "first,\"" + run,
            "first,\"" + run + "\r",
            "first,\"" + run + "\r\n",
            "first,\"" + run + "\"\""
        })
        {
            var path = await directory.WriteAsync("quoted-span.csv", text);
            AssertEquivalent(await CaptureReferenceAsync(path), await CaptureActualAsync(path));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(32768)]
    public async Task QuotedSpanBudgetRetainsCompletedPrefixAndFirstFailure(int limit)
    {
        using var directory = new SearchTestDirectory();
        foreach (var length in new[] { limit, limit + 1 })
        {
            foreach (var suffix in new[] { "\",tail", "\"\"x\",tail", "\r\nlast\",tail", "" })
            {
                var path = await directory.WriteAsync("quoted-budget.csv", ",\"" + new string('x', length) + suffix);
                AssertEquivalent(await CaptureReferenceAsync(path, limit), await CaptureActualAsync(path, limit));
            }
        }
    }

    private static ExtractionContext Context(int limit) =>
        new(_ => { }, limits: new() { MaxSegmentCharacters = limit });

    private static Task<Capture> CaptureActualAsync(string path, int limit = 100000) =>
        CaptureAsync(new CsvTextExtractor().ExtractAsync(path, Context(limit)));

    private static async Task<Capture> CaptureReferenceAsync(string path, int limit = 100000,
        CancellationTokenSource? cancelAfterFirst = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: (int)Math.Clamp(stream.Length, 1024, 16384));
        var source = (IAsyncEnumerable<TextSegment>)ReferenceParser.Invoke(null,
            [reader, limit, cancelAfterFirst?.Token ?? default])!;
        return await CaptureAsync(source, cancelAfterFirst);
    }

    private static async Task<Capture> CaptureAsync(IAsyncEnumerable<TextSegment> source,
        CancellationTokenSource? cancelAfterFirst = null)
    {
        var segments = new List<TextSegment>();
        try
        {
            await foreach (var segment in source)
            {
                segments.Add(segment);
                if (segments.Count == 1) cancelAfterFirst?.Cancel();
            }
            return new(segments, null);
        }
        catch (Exception exception)
        {
            return new(segments, exception is SearchResourceLimitException limit
                ? new Failure(exception.GetType().FullName!, exception.Message, limit.LimitName, limit.Limit)
                : new Failure(exception.GetType().FullName!, exception.Message));
        }
    }

    private static void AssertEquivalent(Capture expected, Capture actual)
    {
        Assert.Equal<TextSegment>(expected.Segments, actual.Segments);
        Assert.Equal(expected.Failure, actual.Failure);
    }

    private sealed record Capture(List<TextSegment> Segments, Failure? Failure);
    private sealed record Failure(string Type, string Message, string? LimitName = null, int? Limit = null);
}
