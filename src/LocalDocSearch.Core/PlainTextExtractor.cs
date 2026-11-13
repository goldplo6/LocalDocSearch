using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace LocalDocSearch.Core;

public sealed class PlainTextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".txt", ".md", ".log" };

    public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opened = await UnicodeTextReader.OpenBufferedAsync(path, context, cancellationToken).ConfigureAwait(false);
        using var reader = opened.Reader;
        var limit = context.Limits.MaxSegmentCharacters;
        var bufferLength = Math.Min(Math.Min(8192, opened.BufferSize), limit);
        // Most files have short lines: avoid two large allocations for every small file.
        var line = new StringBuilder(Math.Min(256, limit));
        long number = 0;
        var skipLf = false;
        var buffer = ArrayPool<char>.Shared.Rent(bufferLength);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await reader.ReadAsync(buffer.AsMemory(0, bufferLength), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                var position = 0;
                while (position < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (skipLf)
                    {
                        skipLf = false;
                        if (buffer[position] == '\n') { position++; continue; }
                    }
                    var separator = buffer.AsSpan(position, count - position).IndexOfAny('\r', '\n');
                    var length = separator < 0 ? count - position : separator;
                    // Validate before growing either the builder or a complete line string.
                    if (length > limit - line.Length)
                        throw new SearchResourceLimitException(nameof(SearchResourceLimits.MaxSegmentCharacters), limit);
                    if (separator >= 0)
                    {
                        string text;
                        if (line.Length == 0) text = new string(buffer, position, length);
                        else
                        {
                            line.Append(buffer, position, length);
                            text = line.ToString();
                            line.Clear();
                        }
                        position += length;
                        skipLf = buffer[position++] == '\r';
                        number++;
                        yield return new TextSegment($"line:{number}", text, new SourceLocation(number, 1));
                    }
                    else
                    {
                        line.Append(buffer, position, length);
                        position = count;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > 0)
            {
                number++;
                yield return new TextSegment($"line:{number}", line.ToString(), new SourceLocation(number, 1));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal static class UnicodeTextReader
{
    // A 1 KiB floor amortizes reads for tiny files; larger files retain the 16 KiB read scale.
    private const int MinReaderBufferSize = 1024;
    private const int MaxReaderBufferSize = 16 * 1024;
    private const int FileBufferSize = 4 * 1024;

    internal static async Task<StreamReader> OpenAsync(string path, ExtractionContext context, CancellationToken token) =>
        (await OpenBufferedAsync(path, context, token).ConfigureAwait(false)).Reader;

    internal static async Task<(StreamReader Reader, int BufferSize)> OpenBufferedAsync(
        string path, ExtractionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(context.EncodingMode))
            throw new ArgumentOutOfRangeException(nameof(context), "请选择有效的文本编码模式。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            // Length is a buffer-size hint, never an EOF or completeness check. A file may grow
            // or shrink after this snapshot; validation and extraction still read through EOF.
            var bufferSize = CalculateReaderBufferSize(stream.Length);
            var prefix = new byte[4];
            var count = 0;
            while (count < prefix.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(prefix.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            var (encoding, bomLength) = DetectEncoding(prefix, count);
            if (bomLength == 0 && context.EncodingMode == TextEncodingMode.Utf8OrGb18030)
            {
                try
                {
                    await ValidateAsync(stream, encoding, bufferSize, token).ConfigureAwait(false);
                }
                catch (DecoderFallbackException)
                {
                    token.ThrowIfCancellationRequested();
                    encoding = StrictGb18030();
                    await ValidateAsync(stream, encoding, bufferSize, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    context.Warn("无 BOM 文件的严格 UTF-8 预验证失败；已回退为严格 GB18030 解码。编码解释可能有歧义，源文件未改写。", SearchIssueCode.Encoding);
                }
            }
            else if (bomLength == 0 && context.EncodingMode == TextEncodingMode.Gb18030)
            {
                encoding = StrictGb18030();
                await ValidateAsync(stream, encoding, bufferSize, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            stream.Position = bomLength;
            return (new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: bufferSize), bufferSize);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Encoding StrictGb18030()
    {
        // System.Text.Encoding.CodePages is supplied by the net10 shared framework.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static int CalculateReaderBufferSize(long length) =>
        (int)Math.Clamp(length, MinReaderBufferSize, MaxReaderBufferSize);

    private static async Task ValidateAsync(FileStream stream, Encoding encoding, int bufferSize, CancellationToken token)
    {
        stream.Position = 0;
        // A separate reader keeps decoder state across buffers and flushes trailing bytes at EOF.
        // Only validation happens here: no segments or matches can escape before the selected
        // encoding is known to be valid. The same read-only stream is then rewound for extraction.
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false,
            bufferSize: bufferSize, leaveOpen: true);
        var buffer = ArrayPool<char>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(buffer.AsMemory(0, bufferSize), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (read == 0) return;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes, int count)
    {
        if (count >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
            return (new UTF32Encoding(false, false, true), 4);
        if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
            return (new UTF32Encoding(true, false, true), 4);
        if (count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return (new UTF8Encoding(false, true), 3);
        if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            return (new UnicodeEncoding(false, false, true), 2);
        if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }
}
