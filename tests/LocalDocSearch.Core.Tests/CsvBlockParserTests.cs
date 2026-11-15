using System.Runtime.CompilerServices;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class CsvBlockParserTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData(",", new[] { "", "" })]
    [InlineData(",,", new[] { "", "", "" })]
    [InlineData("\n", new[] { "" })]
    [InlineData("\r", new[] { "" })]
    [InlineData("\r\n", new[] { "" })]
    [InlineData("\r\r\n", new[] { "", "" })]
    [InlineData("\"\"", new[] { "" })]
    [InlineData("\"\",", new[] { "", "" })]
    [InlineData("a,", new[] { "a", "" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\n,", new[] { "a", "", "" })]
    [InlineData("\"a,b\",\"c\"\"d\"", new[] { "a,b", "c\"d" })]
    public async Task EmptyFieldsAndTrailingRecordsMatchTheOriginal(string text, string[] expected)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("empty-and-tail.csv", text);
        var actual = await CaptureActualAsync(path);
        Assert.Null(actual.Failure);
        Assert.Equal(expected, actual.Segments.Select(segment => segment.Text));
        AssertEquivalent(await CaptureReferenceAsync(path), actual);
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public async Task TokensAroundTheCharacterBufferBoundaryMatchTheOriginal(int position)
    {
        using var directory = new SearchTestDirectory();
        var unquoted = new string('x', position);
        var quoted = "\"" + new string('x', position - 1);
        // The first code unit of each token is at the requested zero-based position.
        var inputs = new[]
        {
            unquoted + "\rZ,tail", unquoted + "\r\nZ,tail", unquoted + "\r\r\nZ,tail",
            unquoted + "\r", unquoted + "\r\n", unquoted + ",tail",
            quoted + "\rZ\",tail", quoted + "\r\nZ\",tail", quoted + "\r\r\nZ\",tail",
            quoted + "\"\"Z\",tail", quoted + "\",tail", quoted + "😀\",tail",
            new string('x', position - 1) + ",\"Z\",tail",
            unquoted + "\"bad", quoted + "\"bad", quoted + "\"\"",
            "ok," + new string('x', position - 3) + "\r\n\"unterminated"
        };
        for (var index = 0; index < inputs.Length; index++)
        {
            var path = await directory.WriteAsync("boundary.csv", inputs[index]);
            AssertEquivalent(await CaptureReferenceAsync(path), await CaptureActualAsync(path),
                $"boundary={position}, input={index}");
        }
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public async Task PhysicalLinesAndLogicalFieldIdsRemainExactAcrossBlocks(int position)
    {
        using var directory = new SearchTestDirectory();
        var prefix = new string('x', position);
        var path = await directory.WriteAsync("locations.csv", prefix + "\r\n\"a\r\nb\",,\"\"\"\"\rZ,");
        var actual = await CaptureActualAsync(path);
        Assert.Null(actual.Failure);
        Assert.Equal<TextSegment>(new[]
        {
            Segment(1, 1, 1, prefix), Segment(2, 1, 2, "a\nb"), Segment(2, 2, 3, ""),
            Segment(2, 3, 3, "\""), Segment(3, 1, 4, "Z"), Segment(3, 2, 4, "")
        }, actual.Segments);
        AssertEquivalent(await CaptureReferenceAsync(path), actual);
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public async Task BudgetIsCheckedIncrementallyForNormalizedAndUnescapedUtf16Values(int length)
    {
        using var directory = new SearchTestDirectory();
        var values = new[]
        {
            new string('x', length),
            "\"" + new string('x', length - 1) + "\"\"\"",
            "\"" + new string('x', length - 1) + "\r\n\"",
            "\"" + new string('x', length - 2) + "😀\""
        };
        foreach (var value in values)
        {
            var path = await directory.WriteAsync("budget.csv", "ok," + value + ",tail");
            var exact = await CaptureActualAsync(path, length);
            Assert.Null(exact.Failure);
            Assert.Equal(3, exact.Segments.Count);
            Assert.Equal(length, exact.Segments[1].Text.Length);
            AssertEquivalent(await CaptureReferenceAsync(path, length), exact);

            var over = await CaptureActualAsync(path, length - 1);
            Assert.Equal("ok", Assert.Single(over.Segments).Text);
            Assert.Equal(new Failure(typeof(SearchResourceLimitException).FullName!,
                $"文件资源限额 MaxSegmentCharacters={length - 1} 已超出；该文件的结果已全部丢弃。",
                nameof(SearchResourceLimits.MaxSegmentCharacters), length - 1), over.Failure);
            AssertEquivalent(await CaptureReferenceAsync(path, length - 1), over);
        }
    }

    [Theory]
    [InlineData("ok,\"unterminated", "CSV 格式错误：记录 1、列 2 的引号字段未闭合。")]
    [InlineData("ok,unquoted\"quote", "CSV 格式错误：记录 1、列 2 的未加引号字段中出现双引号。")]
    [InlineData("ok,\"closed\"tail", "CSV 格式错误：记录 1、列 2 的结束引号后须为逗号或换行。")]
    [InlineData("ok\r\n\"closed\"tail", "CSV 格式错误：记录 2、列 1 的结束引号后须为逗号或换行。")]
    public async Task LateErrorsPreserveTheirMessageAndAlreadyYieldedPrefix(string text, string message)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("malformed.csv", text);
        var actual = await CaptureActualAsync(path);
        Assert.Equal("ok", Assert.Single(actual.Segments).Text);
        Assert.Equal(new Failure(typeof(ExtractionException).FullName!, message), actual.Failure);
        AssertEquivalent(await CaptureReferenceAsync(path), actual);
    }

    [Fact]
    public async Task DeterministicLegalAndArbitraryInputsMatchAllSegmentsAndFailures()
    {
        using var directory = new SearchTestDirectory();
        var random = new Random(0x435356);
        var atoms = new[] { "a", "西", "😀", ",", "\"", "\r", "\n", "\r\n" };
        for (var sample = 0; sample < 192; sample++)
        {
            var text = sample % 2 == 0 ? CreateValidCsv(random, atoms) : CreateArbitraryText(random, atoms);
            var path = await directory.WriteAsync("differential.csv", text);
            var actual = await CaptureActualAsync(path);
            if (sample % 2 == 0) Assert.Null(actual.Failure);
            AssertEquivalent(await CaptureReferenceAsync(path), actual, $"sample={sample}");
            var limit = random.Next(1, 18);
            AssertEquivalent(await CaptureReferenceAsync(path, limit), await CaptureActualAsync(path, limit),
                $"sample={sample}, limit={limit}");
        }
    }

    [Theory]
    [InlineData("first,second,third")]
    [InlineData("first\nsecond\nthird")]
    [InlineData("\"first\"\r\nsecond")]
    [InlineData("first,")]
    [InlineData("first\r")]
    [InlineData("first")]
    public async Task CancellationAfterYieldNeverEmitsAnotherBufferedField(string text)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("cancel.csv", text);
        using var actualCancellation = new CancellationTokenSource();
        var actual = await CaptureAfterFirstAsync(new CsvTextExtractor().ExtractAsync(path,
            new ExtractionContext(_ => { }), actualCancellation.Token), actualCancellation);
        using var referenceCancellation = new CancellationTokenSource();
        ParseCapture reference;
        using (var reader = OpenReferenceReader(path))
            reference = await CaptureAfterFirstAsync(ReferenceExtractAsync(reader, 20_000, referenceCancellation.Token),
                referenceCancellation);
        Assert.Equal("first", Assert.Single(actual.Segments).Text);
        AssertEquivalent(reference, actual);
        if (text != "first")
        {
            Assert.NotNull(actual.Failure);
            Assert.Contains(actual.Failure.Type, new[]
            {
                typeof(OperationCanceledException).FullName, typeof(TaskCanceledException).FullName
            });
        }
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task CancellationBeforeStartingDoesNotEmitAnEmptyField()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("pre-cancel.csv", ",,");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var actual = await CaptureAsync(new CsvTextExtractor().ExtractAsync(path,
            new ExtractionContext(_ => { }), cancellation.Token));
        Assert.Empty(actual.Segments);
        Assert.Equal(typeof(OperationCanceledException).FullName, actual.Failure?.Type);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    [InlineData("gb18030")]
    [InlineData("fallback")]
    public async Task EncodingSelectionAndWarningsStillWrapTheSameParser(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = name switch
        {
            "utf16-le" => new UnicodeEncoding(false, true, true),
            "utf16-be" => new UnicodeEncoding(true, true, true),
            "utf32-le" => new UTF32Encoding(false, true, true),
            "utf32-be" => new UTF32Encoding(true, true, true),
            "gb18030" or "fallback" => Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => new UTF8Encoding(true, true)
        };
        var mode = name switch
        {
            "gb18030" => TextEncodingMode.Gb18030,
            "fallback" => TextEncodingMode.Utf8OrGb18030,
            _ => TextEncodingMode.StrictUnicode
        };
        using var directory = new SearchTestDirectory();
        var text = "\"" + new string('x', 8190) + "😀\r\n西瓜\"\"\",尾,";
        var path = await directory.WriteAsync("encoding.csv", text, encoding);
        var warnings = new List<string>();
        var actual = await CaptureAsync(new CsvTextExtractor().ExtractAsync(path, new ExtractionContext(warnings.Add, mode)));
        Assert.Null(actual.Failure);
        AssertEquivalent(await CaptureReferenceAsync(path, encoding: encoding), actual);
        Assert.Equal(name == "fallback" ? 1 : 0, warnings.Count);
        if (name == "fallback") Assert.Contains("已回退为严格 GB18030", warnings[0]);
    }

    [Fact]
    public async Task LateDecoderFailureKeepsTheOriginalExceptionAndYieldedPrefix()
    {
        using var directory = new SearchTestDirectory();
        var bytes = Encoding.UTF8.GetBytes("ok\n" + new string('x', 65_536)).Concat(new byte[] { 0xc3, 0x28 }).ToArray();
        var path = await directory.WriteBytesAsync("late-decoder.csv", bytes);
        var actual = await CaptureActualAsync(path, 100_000);
        Assert.Equal("ok", Assert.Single(actual.Segments).Text);
        Assert.Equal(typeof(DecoderFallbackException).FullName, actual.Failure?.Type);
        AssertEquivalent(await CaptureReferenceAsync(path, 100_000), actual);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("budget")]
    [InlineData("decoder")]
    public async Task SearchDoesNotPublishAnyPartOfACsvThatFailsAfterAnEarlierMatch(string failure)
    {
        using var directory = new SearchTestDirectory();
        await directory.WriteAsync("good.txt", "needle");
        if (failure == "decoder")
        {
            var bytes = Encoding.UTF8.GetBytes("needle\n" + new string('x', 65_536))
                .Concat(new byte[] { 0xc3, 0x28 }).ToArray();
            await directory.WriteBytesAsync("late.csv", bytes);
        }
        else
        {
            await directory.WriteAsync("late.csv", "needle\n" +
                (failure == "format" ? "\"unterminated" : new string('x', 8193)));
        }
        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var engine = new TextSearchEngine(limits: new() { MaxSegmentCharacters = 100_000 });
        if (failure == "budget") engine = new TextSearchEngine(limits: new() { MaxSegmentCharacters = 8192 });
        var summary = await engine.SearchAsync(new SearchRequest(directory.Root, ["needle"]),
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(".txt", Assert.Single(results).Extension);
        Assert.Single(issues);
    }

    private static TextSegment Segment(long record, int field, long line, string text) =>
        new($"csv:{record}:{field}", text, new SourceLocator(SourceKind.CsvField, Line: line, Record: record, Field: field));

    private static string CreateArbitraryText(Random random, string[] atoms)
    {
        var text = new StringBuilder();
        var count = random.Next(0, 100);
        for (var index = 0; index < count; index++) text.Append(atoms[random.Next(atoms.Length)]);
        return text.ToString();
    }

    private static string CreateValidCsv(Random random, string[] atoms)
    {
        var text = new StringBuilder();
        var records = random.Next(1, 6);
        for (var record = 0; record < records; record++)
        {
            var fields = random.Next(1, 6);
            for (var field = 0; field < fields; field++)
            {
                if (field > 0) text.Append(',');
                var value = new StringBuilder();
                var length = random.Next(0, 15);
                for (var index = 0; index < length; index++) value.Append(atoms[random.Next(atoms.Length)]);
                var raw = value.ToString();
                if (random.Next(2) == 0 && raw.IndexOfAny([',', '"', '\r', '\n']) < 0) text.Append(raw);
                else text.Append('"').Append(raw.Replace("\"", "\"\"")).Append('"');
            }
            if (record + 1 < records || random.Next(2) == 0)
                text.Append(new[] { "\r", "\n", "\r\n" }[random.Next(3)]);
        }
        return text.ToString();
    }

    private static Task<ParseCapture> CaptureActualAsync(string path, int limit = 20_000) =>
        CaptureAsync(new CsvTextExtractor().ExtractAsync(path,
            new ExtractionContext(_ => { }, limits: new() { MaxSegmentCharacters = limit })));

    private static async Task<ParseCapture> CaptureReferenceAsync(string path, int limit = 20_000, Encoding? encoding = null)
    {
        using var reader = OpenReferenceReader(path, encoding);
        return await CaptureAsync(ReferenceExtractAsync(reader, limit));
    }

    private static StreamReader OpenReferenceReader(string path, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false, true);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bufferSize = (int)Math.Clamp(stream.Length, 1024, 16 * 1024);
        stream.Position = encoding.GetPreamble().Length;
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize);
    }

    private static async Task<ParseCapture> CaptureAsync(IAsyncEnumerable<TextSegment> source)
    {
        var segments = new List<TextSegment>();
        try
        {
            await foreach (var segment in source) segments.Add(segment);
            return new(segments, null);
        }
        catch (Exception exception)
        {
            return new(segments, Failure.From(exception));
        }
    }

    private static async Task<ParseCapture> CaptureAfterFirstAsync(IAsyncEnumerable<TextSegment> source,
        CancellationTokenSource cancellation)
    {
        var segments = new List<TextSegment>();
        await using var iterator = source.GetAsyncEnumerator(cancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        segments.Add(iterator.Current);
        cancellation.Cancel();
        try
        {
            while (await iterator.MoveNextAsync()) segments.Add(iterator.Current);
            return new(segments, null);
        }
        catch (Exception exception)
        {
            return new(segments, Failure.From(exception));
        }
    }

    private static void AssertEquivalent(ParseCapture expected, ParseCapture actual, string? because = null)
    {
        Assert.True(expected.Segments.SequenceEqual(actual.Segments), because ?? "CSV segment content, IDs, and source locations");
        Assert.Equal(expected.Failure, actual.Failure);
    }

    private sealed record ParseCapture(List<TextSegment> Segments, Failure? Failure);
    private sealed record Failure(string Type, string Message, string? LimitName = null, int? Limit = null)
    {
        public static Failure From(Exception exception) => exception is SearchResourceLimitException limit
            ? new(exception.GetType().FullName!, exception.Message, limit.LimitName, limit.Limit)
            : new(exception.GetType().FullName!, exception.Message);
    }

    // Frozen parser/character-reader algorithm from the supplied pre-optimization snapshot.
    // Only reader opening is supplied by the fixture; the reference never calls the new parser.
    private static async IAsyncEnumerable<TextSegment> ReferenceExtractAsync(StreamReader reader, int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = new ReferenceCsvCharReader(reader);
        var value = new StringBuilder();
        var state = ReferenceFieldState.Start;
        long record = 1, fieldStartLine = 1;
        var field = 1;
        var pendingRecord = false;
        while (await input.ReadAsync(cancellationToken).ConfigureAwait(false) is var current && current >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = (char)current;
            pendingRecord = true;
            if (state == ReferenceFieldState.Quoted)
            {
                if (character == '"') state = ReferenceFieldState.AfterQuote;
                else ReferenceAppend(value, character, limit);
                continue;
            }
            if (character is ',' or '\n')
            {
                yield return new TextSegment($"csv:{record}:{field}", value.ToString(),
                    new SourceLocator(SourceKind.CsvField, Line: fieldStartLine, Record: record, Field: field));
                value.Clear();
                state = ReferenceFieldState.Start;
                fieldStartLine = input.Line;
                if (character == ',') field++;
                else { record++; field = 1; pendingRecord = false; }
                continue;
            }
            if (character == '"')
            {
                if (state == ReferenceFieldState.Start) state = ReferenceFieldState.Quoted;
                else if (state == ReferenceFieldState.AfterQuote) { ReferenceAppend(value, '"', limit); state = ReferenceFieldState.Quoted; }
                else throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的未加引号字段中出现双引号。");
                continue;
            }
            if (state == ReferenceFieldState.AfterQuote)
                throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的结束引号后须为逗号或换行。");
            ReferenceAppend(value, character, limit);
            state = ReferenceFieldState.Unquoted;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (state == ReferenceFieldState.Quoted)
            throw new ExtractionException($"CSV 格式错误：记录 {record}、列 {field} 的引号字段未闭合。");
        if (pendingRecord)
            yield return new TextSegment($"csv:{record}:{field}", value.ToString(),
                new SourceLocator(SourceKind.CsvField, Line: fieldStartLine, Record: record, Field: field));
    }

    private static void ReferenceAppend(StringBuilder value, char character, int limit)
    {
        if (value.Length >= limit)
            throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
        value.Append(character);
    }

    private enum ReferenceFieldState { Start, Unquoted, Quoted, AfterQuote }

    private sealed class ReferenceCsvCharReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[8192];
        private int _position, _count, _pending = -1;
        public long Line { get; private set; } = 1;

        public async ValueTask<int> ReadAsync(CancellationToken token)
        {
            var current = await ReadRawAsync(token).ConfigureAwait(false);
            if (current == '\r')
            {
                var next = await ReadRawAsync(token).ConfigureAwait(false);
                if (next != '\n') _pending = next;
                current = '\n';
            }
            if (current == '\n') Line++;
            return current;
        }

        private async ValueTask<int> ReadRawAsync(CancellationToken token)
        {
            if (_pending >= 0) { var next = _pending; _pending = -1; return next; }
            if (_position >= _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                _position = 0;
                if (_count == 0) return -1;
            }
            return _buffer[_position++];
        }
    }
}
