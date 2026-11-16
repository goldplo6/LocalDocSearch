using System.Runtime.CompilerServices;
using System.Security;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;

namespace LocalDocSearch.Extractors;

internal static class ReadOnlyExtraction
{
    internal const long MaxInputBytes = 128L * 1024 * 1024;
    internal const long MaxPartCharacters = 16L * 1024 * 1024;

    internal static OpenSettings Settings() => new()
    {
        AutoSave = false,
        MaxCharactersInPart = MaxPartCharacters
    };

    // Buffer one file: neither a later malformed part nor cancellation publishes partial content.
    internal static async IAsyncEnumerable<TextSegment> Run(
        string path, ExtractionContext context,
        Func<FileStream, ExtractionContext, CancellationToken, List<TextSegment>> read,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        var segments = Load(path, context, read, token);
        try
        {
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < segments.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var segment = segments[index];
                // Parsing has already succeeded for the whole file. Keep only the
                // current and not-yet-consumed segments alive while searching.
                segments[index] = null!;
                yield return segment;
            }
        }
        finally
        {
            // Dispose/cancellation can stop enumeration before the last segment.
            segments.Clear();
        }
    }

    private static List<TextSegment> Load(string path, ExtractionContext context,
        Func<FileStream, ExtractionContext, CancellationToken, List<TextSegment>> read, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            token.ThrowIfCancellationRequested();
            if (stream.Length > MaxInputBytes)
                throw new ExtractionException("文档超过 128 MiB 提取上限，已停止读取；未截断内容。");
            var segments = read(stream, context, token);
            token.ThrowIfCancellationRequested();
            if (stream.Length > MaxInputBytes)
                throw new ExtractionException("文档读取期间超过 128 MiB 提取上限。");
            return segments;
        }
        catch (ExtractionException) { throw; }
        catch (Exception exception) when (IsExpected(exception))
        {
            token.ThrowIfCancellationRequested();
            throw new ExtractionException("文档提取失败：" + exception.Message, exception);
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException) &&
        (exception is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or XmlException or
            ArgumentException or InvalidOperationException or NotSupportedException or FormatException or
            OverflowException or KeyNotFoundException or IndexOutOfRangeException ||
            exception.GetType().Namespace?.StartsWith("DocumentFormat.OpenXml", StringComparison.Ordinal) == true ||
            exception.GetType().Namespace?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true ||
            exception.GetType().Namespace?.StartsWith("System.IO.Packaging", StringComparison.Ordinal) == true);
}
