using System.Buffers.Binary;
using System.IO.Compression;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class OpenXmlPackageBudgetTests
{
    [Fact]
    public void ExactEntryTotalAndCountLimits_LeaveTheStreamOpenAndRewound()
    {
        using var stream = Package(8, 5);
        OpenXmlPackageBudget.Validate(stream, CancellationToken.None, maxEntryBytes: 8, maxTotalBytes: 13, maxEntries: 2);
        Assert.Equal(0L, stream.Position);
        Assert.True(stream.CanRead);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Fact]
    public void OversizedEntry_IsRejectedBeforeItsPayloadIsOpened()
    {
        using var stream = Package(9);
        var error = Assert.Throws<ExtractionException>(() =>
            OpenXmlPackageBudget.Validate(stream, CancellationToken.None, maxEntryBytes: 8));
        Assert.Contains("单条目展开", error.Message);
        Assert.Equal(0L, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void IndividuallyAllowedEntries_StillConsumeACumulativeBudget()
    {
        using var stream = Package(8, 5);
        var error = Assert.Throws<ExtractionException>(() =>
            OpenXmlPackageBudget.Validate(stream, CancellationToken.None, maxEntryBytes: 8, maxTotalBytes: 12));
        Assert.Contains("累计展开", error.Message);
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void EmptyEntries_StillConsumeTheEntryCountBudget()
    {
        using var stream = Package(0, 0, 0);
        var error = Assert.Throws<ExtractionException>(() =>
            OpenXmlPackageBudget.Validate(stream, CancellationToken.None, maxEntries: 2));
        Assert.Contains("条目数量", error.Message);
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void PreCancellation_WinsBeforeInvalidZipMetadataIsRead()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => OpenXmlPackageBudget.Validate(stream, cancellation.Token));
        Assert.Equal(0L, stream.Position);
    }

    [Fact]
    public void MalformedZipMetadata_RestoresPositionAndDoesNotOwnTheStream()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => OpenXmlPackageBudget.Validate(stream, CancellationToken.None));
        Assert.Equal(0L, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void NonPositiveBudgets_AreRejected(long entry, long total, int count)
    {
        using var stream = Package(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenXmlPackageBudget.Validate(stream, CancellationToken.None, entry, total, count));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    public async Task EveryOfficeExtractor_RejectsOversizedDeclaredEntryBeforeSdkParsingAndPublishesNothing(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("declared-bomb" + extension);
        using var package = Package(1);
        var bytes = package.ToArray();
        var directoryOffset = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        Assert.True(directoryOffset >= 0);
        // A tiny fixture declares a huge uncompressed entry: exercise the real preflight
        // without allocating or decompressing a large payload. It is not a valid Office file,
        // so the ZIP-limit message also proves rejection happened before SDK parsing.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset + 24, 4),
            checked((uint)(OpenXmlPackageBudget.MaxEntryBytes + 1)));
        File.WriteAllBytes(path, bytes);
        var modified = File.GetLastWriteTimeUtc(path);
        var published = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<ExtractionException>(async () =>
        {
            await foreach (var segment in ExtractionTestSupport.For(extension).ExtractAsync(path, new ExtractionContext(_ => { })))
                published.Add(segment);
        });
        Assert.Contains("ZIP 单条目展开", error.Message);
        Assert.Empty(published);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, exclusive.Length);
    }

    [Fact]
    public void FixedPackageBudgets_AreExplicitAndSeparateFromOutputCharacters()
    {
        Assert.Equal(64L * 1024 * 1024, OpenXmlPackageBudget.MaxEntryBytes);
        Assert.Equal(256L * 1024 * 1024, OpenXmlPackageBudget.MaxTotalBytes);
        Assert.Equal(10_000, OpenXmlPackageBudget.MaxEntries);
        Assert.Equal(32L * 1024 * 1024, BoundedSegmentBuffer.DefaultMaxCharacters);
    }

    private static MemoryStream Package(params int[] lengths)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            for (var index = 0; index < lengths.Length; index++)
            {
                using var entry = archive.CreateEntry($"part{index}.xml", CompressionLevel.Optimal).Open();
                entry.Write(new byte[lengths[index]]);
            }
        stream.Position = 0;
        return stream;
    }
}
