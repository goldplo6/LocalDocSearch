using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class TextDecodingFastPathTests
{
    private const string FallbackWarning = "无 BOM 文件的严格 UTF-8 预验证失败；已回退为严格 GB18030 解码。编码解释可能有歧义，源文件未改写。";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(16383)]
    [InlineData(65535)]
    public async Task Utf8MultibyteBoundariesWhitespaceAndEmptyLinesMatchFrozenReader(int length)
    {
        using var directory = new SearchTestDirectory();
        foreach (var text in new[]
        {
            new string(' ', length), new string('x', length) + "😀西\r\n\n𠮷\r\n",
            new string('x', length) + "\r\n\rnext\n", "\n\r\n\r"
        })
        {
            var path = await directory.WriteAsync("utf8.txt", text);
            foreach (var mode in new[] { TextEncodingMode.StrictUnicode, TextEncodingMode.Utf8OrGb18030 })
            {
                var actual = await CaptureActualAsync(path, mode);
                Assert.Null(actual.Failure);
                Assert.Empty(actual.Warnings);
                AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);
            }
        }
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task BomSelectionAndInvalidEofTakePriorityOverEveryRequestedMode(string name)
    {
        using var directory = new SearchTestDirectory();
        var encoding = BomEncoding(name);
        var valid = encoding.GetPreamble().Concat(encoding.GetBytes("first\r\n" + new string('x', 16377) + "😀西\n\n")).ToArray();
        var invalid = name == "utf8" ? new byte[] { 0xc3 } : new byte[] { 0x81 };
        foreach (var mode in Enum.GetValues<TextEncodingMode>())
        {
            var path = await directory.WriteBytesAsync("bom.txt", valid);
            var actual = await CaptureActualAsync(path, mode);
            Assert.Null(actual.Failure);
            Assert.Empty(actual.Warnings);
            AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);

            path = await directory.WriteBytesAsync("bom-invalid.txt", [.. valid, .. invalid]);
            actual = await CaptureActualAsync(path, mode);
            Assert.Equal(typeof(DecoderFallbackException).FullName, actual.Failure?.Type);
            Assert.Empty(actual.Warnings);
            AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);
        }
    }

    [Theory]
    [InlineData(TextEncodingMode.Utf8OrGb18030)]
    [InlineData(TextEncodingMode.Gb18030)]
    public async Task LegacyFourByteSplitsAndInvalidEofMatchFrozenPrevalidation(TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var gb = StrictGb18030();
        foreach (var length in new[] { 0, 1023, 16383, 65535 })
        {
            var valid = gb.GetBytes(new string('x', length) + "𠮷西瓜\r\n\nend");
            var path = await directory.WriteBytesAsync("gb.txt", valid);
            var actual = await CaptureActualAsync(path, mode);
            Assert.Null(actual.Failure);
            Assert.Equal(mode == TextEncodingMode.Utf8OrGb18030 ? new[] { FallbackWarning } : [], actual.Warnings);
            AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);

            foreach (var tail in new byte[][] { [0x81], [0x81, 0x7f], [0x81, 0x30, 0x81] })
            {
                path = await directory.WriteBytesAsync("gb-invalid.txt", [.. valid, .. tail]);
                actual = await CaptureActualAsync(path, mode);
                Assert.Equal(typeof(DecoderFallbackException).FullName, actual.Failure?.Type);
                Assert.Empty(actual.Segments);
                Assert.Empty(actual.Warnings);
                AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);
            }
        }
    }

    [Theory]
    [InlineData(TextEncodingMode.StrictUnicode)]
    [InlineData(TextEncodingMode.Utf8OrGb18030)]
    [InlineData(TextEncodingMode.Gb18030)]
    public async Task ValidAmbiguousBytesKeepRequestedInterpretation(TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        byte[] bytes = [0xc2, 0xa9];
        var path = await directory.WriteBytesAsync("ambiguous.txt", bytes);
        var actual = await CaptureActualAsync(path, mode);
        var expected = (mode == TextEncodingMode.Gb18030 ? StrictGb18030() : new UTF8Encoding(false, true)).GetString(bytes);
        Assert.Equal(expected, Assert.Single(actual.Segments).Text);
        Assert.Empty(actual.Warnings);
        AssertEquivalent(await CaptureReferenceAsync(path, mode), actual);
    }

    [Fact]
    public async Task StrictLateFailurePreservesAlreadyYieldedPrefixAndExactDecoderMessage()
    {
        using var directory = new SearchTestDirectory();
        var prefix = Encoding.UTF8.GetBytes("first\r\n" + new string('x', 65536));
        foreach (var tail in new byte[][] { [0xc3], [0xc3, 0x28], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80] })
        {
            var path = await directory.WriteBytesAsync("late-invalid.txt", [.. prefix, .. tail]);
            var actual = await CaptureActualAsync(path, TextEncodingMode.StrictUnicode);
            Assert.Equal("first", Assert.Single(actual.Segments).Text);
            Assert.Equal(typeof(DecoderFallbackException).FullName, actual.Failure?.Type);
            AssertEquivalent(await CaptureReferenceAsync(path, TextEncodingMode.StrictUnicode), actual);
        }
    }

    [Theory]
    [InlineData(TextEncodingMode.StrictUnicode)]
    [InlineData(TextEncodingMode.Utf8OrGb18030)]
    [InlineData(TextEncodingMode.Gb18030)]
    public async Task CancellationAtBufferedYieldAndBeforeOpenKeepsTheFrozenContract(TextEncodingMode mode)
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteAsync("cancel.txt", "first\r\n" + new string('x', 65536) + "\nthird");
        using var actualCancellation = new CancellationTokenSource();
        var actual = await CaptureActualAsync(path, mode, actualCancellation, cancelAtFirst: true);
        using var referenceCancellation = new CancellationTokenSource();
        AssertEquivalent(await CaptureReferenceAsync(path, mode, referenceCancellation, cancelAtFirst: true), actual);
        Assert.Equal("first", Assert.Single(actual.Segments).Text);

        using var preActual = new CancellationTokenSource();
        preActual.Cancel();
        using var preReference = new CancellationTokenSource();
        preReference.Cancel();
        var missing = Path.Combine(directory.Root, "missing.txt");
        AssertEquivalent(await CaptureReferenceAsync(missing, mode, preReference),
            await CaptureActualAsync(missing, mode, preActual));
    }

    [Fact]
    public async Task CancellationFromFallbackWarningDoesNotPublishAFirstSegment()
    {
        using var directory = new SearchTestDirectory();
        var path = await directory.WriteBytesAsync("warning-cancel.txt", StrictGb18030().GetBytes("西瓜\nnext"));
        using var actualCancellation = new CancellationTokenSource();
        var actual = await CaptureActualAsync(path, TextEncodingMode.Utf8OrGb18030, actualCancellation, cancelAtWarning: true);
        using var referenceCancellation = new CancellationTokenSource();
        AssertEquivalent(await CaptureReferenceAsync(path, TextEncodingMode.Utf8OrGb18030, referenceCancellation, cancelAtWarning: true), actual);
        Assert.Empty(actual.Segments);
        Assert.Equal(new[] { FallbackWarning }, actual.Warnings);
    }

    private static Task<Capture> CaptureActualAsync(string path, TextEncodingMode mode, CancellationTokenSource? cancellation = null,
        bool cancelAtFirst = false, bool cancelAtWarning = false) =>
        CaptureAsync(path, mode, reference: false, cancellation, cancelAtFirst, cancelAtWarning);

    private static Task<Capture> CaptureReferenceAsync(string path, TextEncodingMode mode, CancellationTokenSource? cancellation = null,
        bool cancelAtFirst = false, bool cancelAtWarning = false) =>
        CaptureAsync(path, mode, reference: true, cancellation, cancelAtFirst, cancelAtWarning);

    private static async Task<Capture> CaptureAsync(string path, TextEncodingMode mode, bool reference,
        CancellationTokenSource? cancellation, bool cancelAtFirst, bool cancelAtWarning)
    {
        var segments = new List<TextSegment>();
        var warnings = new List<string>();
        var context = new ExtractionContext(warning =>
        {
            warnings.Add(warning);
            if (cancelAtWarning) cancellation!.Cancel();
        }, mode, limits: new() { MaxSegmentCharacters = 100000 });
        var token = cancellation?.Token ?? default;
        try
        {
            var source = reference ? ReferenceExtractAsync(path, context, token) : new PlainTextExtractor().ExtractAsync(path, context, token);
            await foreach (var segment in source)
            {
                segments.Add(segment);
                if (cancelAtFirst && segments.Count == 1) cancellation!.Cancel();
            }
            return new(segments, warnings, null);
        }
        catch (Exception exception)
        {
            return new(segments, warnings, new(exception.GetType().FullName!, exception.Message));
        }
    }

    private static void AssertEquivalent(Capture expected, Capture actual)
    {
        Assert.Equal<TextSegment>(expected.Segments, actual.Segments);
        Assert.Equal<string>(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.Failure, actual.Failure);
    }

    private sealed record Capture(List<TextSegment> Segments, List<string> Warnings, Failure? Failure);
    private sealed record Failure(string Type, string Message);

    // Frozen rc11 reader opening, prevalidation and line extraction. No production reader helper is used.
    private static async IAsyncEnumerable<TextSegment> ReferenceExtractAsync(string path, ExtractionContext context,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var opened = await OpenReferenceAsync(path, context, token);
        using var reader = opened.Reader;
        var limit = context.Limits.MaxSegmentCharacters;
        var bufferLength = Math.Min(Math.Min(8192, opened.BufferSize), limit);
        var line = new StringBuilder(Math.Min(256, limit));
        long number = 0;
        var skipLf = false;
        var buffer = ArrayPool<char>.Shared.Rent(bufferLength);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await reader.ReadAsync(buffer.AsMemory(0, bufferLength), token);
                if (count == 0) break;
                var position = 0;
                while (position < count)
                {
                    token.ThrowIfCancellationRequested();
                    if (skipLf)
                    {
                        skipLf = false;
                        if (buffer[position] == '\n') { position++; continue; }
                    }
                    var separator = buffer.AsSpan(position, count - position).IndexOfAny('\r', '\n');
                    var length = separator < 0 ? count - position : separator;
                    if (length > limit - line.Length)
                        throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
                    if (separator >= 0)
                    {
                        string text;
                        if (line.Length == 0) text = new string(buffer, position, length);
                        else { line.Append(buffer, position, length); text = line.ToString(); line.Clear(); }
                        position += length;
                        skipLf = buffer[position++] == '\r';
                        number++;
                        yield return new TextSegment($"line:{number}", text, new SourceLocation(number, 1));
                    }
                    else { line.Append(buffer, position, length); position = count; }
                }
            }
            token.ThrowIfCancellationRequested();
            if (line.Length > 0)
            {
                number++;
                yield return new TextSegment($"line:{number}", line.ToString(), new SourceLocation(number, 1));
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    private static async Task<(StreamReader Reader, int BufferSize)> OpenReferenceAsync(string path, ExtractionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(context.EncodingMode))
            throw new ArgumentOutOfRangeException(nameof(context), "请选择有效的文本编码模式。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var bufferSize = (int)Math.Clamp(stream.Length, 1024, 16384);
            var prefix = new byte[4];
            var count = 0;
            while (count < prefix.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(prefix.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            var (encoding, bomLength) = DetectReferenceEncoding(prefix, count);
            if (bomLength == 0 && context.EncodingMode == TextEncodingMode.Utf8OrGb18030)
            {
                try { await ValidateReferenceAsync(stream, encoding, bufferSize, token); }
                catch (DecoderFallbackException)
                {
                    token.ThrowIfCancellationRequested();
                    encoding = StrictGb18030();
                    await ValidateReferenceAsync(stream, encoding, bufferSize, token);
                    token.ThrowIfCancellationRequested();
                    // This fixture uses the public message-only callback; Encoding issue codes
                    // remain covered by the engine-level EncodingModeTests. Both Warn overloads
                    // invoke that same callback when no typed warning handler is registered.
                    context.Warn(FallbackWarning);
                }
            }
            else if (bomLength == 0 && context.EncodingMode == TextEncodingMode.Gb18030)
            {
                encoding = StrictGb18030();
                await ValidateReferenceAsync(stream, encoding, bufferSize, token);
            }
            token.ThrowIfCancellationRequested();
            stream.Position = bomLength;
            return (new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize), bufferSize);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static async Task ValidateReferenceAsync(FileStream stream, Encoding encoding, int bufferSize, CancellationToken token)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize, leaveOpen: true);
        var buffer = ArrayPool<char>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(buffer.AsMemory(0, bufferSize), token);
                token.ThrowIfCancellationRequested();
                if (read == 0) return;
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    private static (Encoding Encoding, int BomLength) DetectReferenceEncoding(byte[] bytes, int count)
    {
        if (count >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            return (new UTF32Encoding(false, false, true), 4);
        if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            return (new UTF32Encoding(true, false, true), 4);
        if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return (new UTF8Encoding(false, true), 3);
        if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return (new UnicodeEncoding(false, false, true), 2);
        if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    private static Encoding BomEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(true, true),
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        _ => new UTF32Encoding(true, true, true)
    };

    private static Encoding StrictGb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
