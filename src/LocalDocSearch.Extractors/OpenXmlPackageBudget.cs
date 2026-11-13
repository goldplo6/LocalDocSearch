using System.IO.Compression;
using LocalDocSearch.Core;

namespace LocalDocSearch.Extractors;

/// <summary>Checks declared ZIP sizes before opening any compressed entry. These are logical
/// package limits, not a hard memory boundary for ZIP metadata, decoders or the OpenXML DOM.
/// Preflight does not provide a consistent snapshot of a concurrently modified file.</summary>
internal static class OpenXmlPackageBudget
{
    internal const long MaxEntryBytes = 64L * 1024 * 1024;
    internal const long MaxTotalBytes = 256L * 1024 * 1024;
    internal const int MaxEntries = 10_000;

    internal static void Validate(Stream stream, CancellationToken token,
        long maxEntryBytes = MaxEntryBytes, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        var position = stream.Position;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            var entries = 0;
            // ZipArchive itself materializes central-directory metadata. No entry payload is
            // opened here; forged sizes and third-party internal allocations remain outside this gate.
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entries >= maxEntries)
                    throw new ExtractionException($"文档 ZIP 条目数量超过上限（{maxEntries:N0}）；未读取压缩条目或发布部分文档。");
                entries++;
                var length = entry.Length;
                if (length > maxEntryBytes)
                    throw new ExtractionException($"文档 ZIP 单条目展开大小超过上限（{maxEntryBytes:N0} 字节）；未读取压缩条目或发布部分文档。");
                if (length > maxTotalBytes - total)
                    throw new ExtractionException($"文档 ZIP 累计展开大小超过上限（{maxTotalBytes:N0} 字节）；未读取压缩条目或发布部分文档。");
                total += length;
            }
            token.ThrowIfCancellationRequested();
        }
        finally { stream.Position = position; }
    }
}
