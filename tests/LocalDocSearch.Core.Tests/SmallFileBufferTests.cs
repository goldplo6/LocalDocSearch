using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class SmallFileBufferTests
{
    private static readonly string[] EncodingNames =
        ["utf8-strict", "utf8", "utf8-bom", "utf16le", "utf16be", "utf32le", "utf32be", "gb18030", "gb18030-fallback"];

    public static IEnumerable<object[]> TinyCases()
    {
        foreach (var extension in new[] { ".txt", ".md", ".log", ".csv" })
            foreach (var encoding in EncodingNames)
                yield return [extension, encoding];
    }

    [Theory]
    [MemberData(nameof(TinyCases))]
    public async Task TinyFilesPreserveUnicodeBomPriorityLegacyFallbackAndLocations(string extension, string encodingName)
    {
        using var directory = new SearchTestDirectory();
        var csv = extension == ".csv";
        var text = csv ? "前🍉目标,\"换\r\n行\"\r\n末尾," : "前🍉目标\r\n\r末尾";
        var path = await directory.WriteAsync("tiny" + extension, text, EncodingFor(encodingName));
        var warnings = new List<string>();
        var segments = await ReadAsync(path, extension, new ExtractionContext(warnings.Add, ModeFor(encodingName)));
        Assert.Equal(csv ? new[] { "前🍉目标", "换\n行", "末尾", "" } : new[] { "前🍉目标", "", "末尾" },
            segments.Select(segment => segment.Text));
        Assert.Equal(csv ? new long[] { 1, 1, 3, 3 } : new long[] { 1, 2, 3 },
            segments.Select(segment => segment.Source.Line));
        Assert.Equal(csv ? new[] { "csv:1:1", "csv:1:2", "csv:2:1", "csv:2:2" } : new[] { "line:1", "line:2", "line:3" },
            segments.Select(segment => segment.Id));
        Assert.Equal(encodingName == "gb18030-fallback" ? 1 : 0, warnings.Count);

        // Offsets count UTF-16 code units even when the source uses UTF-32 or GB18030.
        var capture = new SearchCapture();
        await capture.RunAsync(new SearchRequest(directory.Root, ["目标"], EncodingMode: ModeFor(encodingName)));
        var match = Assert.Single(Assert.Single(capture.Results).Matches);
        Assert.Equal(3, match.Offset);
        Assert.Equal("目标", match.Text);
        if (!csv) Assert.Equal(4, match.Location.Column);
    }

    public static IEnumerable<object[]> BoundaryCases()
    {
        foreach (var extension in new[] { ".txt", ".csv" })
            foreach (var encoding in EncodingNames)
                foreach (var prefix in new[] { 1023, 8190, 8191, 16382, 16383 })
                    yield return [extension, encoding, prefix];
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public async Task DecoderAndLineBoundariesPreserveEmojiCrLfAndUnterminatedTail(string extension, string encodingName, int prefix)
    {
        using var directory = new SearchTestDirectory();
        var first = new string('x', prefix);
        var csv = extension == ".csv";
        var text = csv ? $"\"{first}🍉\r\n引号\"\"末尾\",尾" : first + "\r\n🍉\r\n\r末尾🍉";
        var path = await directory.WriteAsync("boundary" + extension, text, EncodingFor(encodingName));
        var segments = await ReadAsync(path, extension, new ExtractionContext(_ => { }, ModeFor(encodingName)));
        Assert.Equal(csv ? new[] { first + "🍉\n引号\"末尾", "尾" } : new[] { first, "🍉", "", "末尾🍉" },
            segments.Select(segment => segment.Text));
        Assert.Equal(csv ? new long[] { 1, 2 } : new long[] { 1, 2, 3, 4 },
            segments.Select(segment => segment.Source.Line));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(50000)]
    public async Task BufferSizeThresholdsAndLongLinesDoNotCapUnterminatedText(int length)
    {
        using var directory = new SearchTestDirectory();
        var text = new string('x', length);
        foreach (var extension in new[] { ".txt", ".csv" })
        {
            var path = await directory.WriteAsync("length" + extension, text);
            var segments = await ReadAsync(path, extension, new ExtractionContext(_ => { }, limits: new()
            {
                MaxSegmentCharacters = Math.Max(1, length),
            }));
            if (length == 0) Assert.Empty(segments);
            else Assert.Equal(text, Assert.Single(segments).Text);
            if (length <= 1) continue;
            var error = await Assert.ThrowsAsync<SearchResourceLimitException>(() => ReadAsync(path, extension,
                new ExtractionContext(_ => { }, limits: new() { MaxSegmentCharacters = length - 1 })));
            Assert.Equal(length - 1, error.Limit);
            Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), error.LimitName);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task ExactLimitAllowsCrLfToStraddleTheConsumerBuffer(int limit)
    {
        using var directory = new SearchTestDirectory();
        var first = new string('x', limit - 1);
        var full = new string('y', limit);
        var path = await directory.WriteAsync("crlf.txt", first + "\r\n" + full + "\r\nZ");
        var segments = await ReadAsync(path, ".txt", new ExtractionContext(_ => { }, limits: new() { MaxSegmentCharacters = limit }));
        Assert.Equal(new[] { first, full, "Z" }, segments.Select(segment => segment.Text));
    }

    public static IEnumerable<object[]> InvalidBomCases()
    {
        // Each BOM must select its strict decoder, including a partial trailing code unit.
        yield return new object[] { new byte[] { 0xef, 0xbb, 0xbf, 0xf0, 0x9f, 0x8d } };
        yield return new object[] { new byte[] { 0xff, 0xfe, 0x3c, 0xd8 } };
        yield return new object[] { new byte[] { 0xfe, 0xff, 0xd8, 0x3c } };
        yield return new object[] { new byte[] { 0xff, 0xfe, 0, 0, 0x49, 0xf3, 0x01 } };
        yield return new object[] { new byte[] { 0, 0, 0xfe, 0xff, 0, 0x01, 0xf3 } };
    }

    [Theory]
    [MemberData(nameof(InvalidBomCases))]
    public async Task TinyInvalidBomPayloadsFlushStrictDecoderAtEof(byte[] bytes)
    {
        using var directory = new SearchTestDirectory();
        foreach (var extension in new[] { ".txt", ".csv" })
        {
            var path = await directory.WriteBytesAsync("invalid" + extension, bytes);
            foreach (var mode in Enum.GetValues<TextEncodingMode>())
            {
                var warnings = new List<string>();
                await Assert.ThrowsAsync<DecoderFallbackException>(() => ReadAsync(path, extension, new ExtractionContext(warnings.Add, mode)));
                Assert.Empty(warnings);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(bytes.Length, exclusive.Length);
            }
        }
    }

    [Theory]
    [InlineData(1023, TextEncodingMode.Utf8OrGb18030)]
    [InlineData(1023, TextEncodingMode.Gb18030)]
    [InlineData(16383, TextEncodingMode.Utf8OrGb18030)]
    [InlineData(16383, TextEncodingMode.Gb18030)]
    public async Task InvalidLegacyTailIsPrevalidatedBeforeAnySegmentEscapes(int prefix, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var bytes = EncodingFor("gb18030").GetBytes("MARKER\n" + new string('x', prefix) + "\n西瓜");
        foreach (var extension in new[] { ".txt", ".csv" })
        {
            var path = await directory.WriteBytesAsync("legacy-invalid" + extension, [.. bytes, 0x81]);
            var published = new List<TextSegment>();
            var warnings = new List<string>();
            await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            {
                await foreach (var segment in Extractor(extension).ExtractAsync(path, new ExtractionContext(warnings.Add, mode)))
                    published.Add(segment);
            });
            Assert.Empty(published);
            Assert.Empty(warnings);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(bytes.Length + 1, exclusive.Length);
        }
    }

    [Theory]
    [InlineData(".txt", TextEncodingMode.StrictUnicode)]
    [InlineData(".txt", TextEncodingMode.Utf8OrGb18030)]
    [InlineData(".csv", TextEncodingMode.StrictUnicode)]
    [InlineData(".csv", TextEncodingMode.Utf8OrGb18030)]
    public async Task GrowthAfterFirstSegmentIsReadBeyondTheOriginalLength(string extension, TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("grow" + extension, "first\n");
        await using var iterator = Extractor(extension).ExtractAsync(path, new ExtractionContext(_ => { }, mode)).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("first", iterator.Current.Text);
        var tail = new string('x', 20000) + "🍉";
        // Mutation occurs while enumeration is paused, without a timer or a racing task.
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            await writer.WriteAsync(Encoding.UTF8.GetBytes(tail));
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(tail, iterator.Current.Text);
        Assert.False(await iterator.MoveNextAsync());
    }

    private static ITextExtractor Extractor(string extension) => extension == ".csv" ? new CsvTextExtractor() : new PlainTextExtractor();

    private static async Task<List<TextSegment>> ReadAsync(string path, string extension, ExtractionContext context)
    {
        var segments = new List<TextSegment>();
        await foreach (var segment in Extractor(extension).ExtractAsync(path, context)) segments.Add(segment);
        return segments;
    }

    private static TextEncodingMode ModeFor(string name) => name switch
    {
        "utf8-strict" => TextEncodingMode.StrictUnicode,
        "utf8" or "gb18030-fallback" => TextEncodingMode.Utf8OrGb18030,
        _ => TextEncodingMode.Gb18030, // A Unicode BOM must override even forced legacy mode.
    };

    private static Encoding EncodingFor(string name)
    {
        if (name is "gb18030" or "gb18030-fallback")
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        return name switch
        {
            "utf8-strict" or "utf8" => new UTF8Encoding(false, true),
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16le" => new UnicodeEncoding(false, true, true),
            "utf16be" => new UnicodeEncoding(true, true, true),
            "utf32le" => new UTF32Encoding(false, true, true),
            "utf32be" => new UTF32Encoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
