using System.Security.Cryptography;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class ConcurrentExtractorTests(SampleCorpus corpus) : IClassFixture<SampleCorpus>
{
    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".log")]
    [InlineData(".csv")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public Task TwoWorkers_SharingOneExtractor_MatchSerialExtraction(string extension) =>
        VerifySharedExtractorAsync(extension, workerCount: 2);

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".log")]
    [InlineData(".csv")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public Task FourWorkers_SharingOneExtractor_MatchSerialExtraction(string extension) =>
        VerifySharedExtractorAsync(extension, workerCount: 4);

    private async Task VerifySharedExtractorAsync(string extension, int workerCount)
    {
        using var directory = new TestDirectory();
        var paths = Enumerable.Range(0, 4)
            .Select(index => directory.FileFor($"independent-{index}{extension}"))
            .ToArray();
        foreach (var path in paths) File.Copy(corpus.FileFor(extension), path);

        // Resolve once: both the serial baseline and every worker use this exact instance.
        var registry = DocumentExtractorCatalog.CreateDefault();
        var extractor = registry.Single(item => item.Extensions.Contains(extension));
        var originals = paths.Select(Snapshot).ToArray();
        var expected = new ExtractionSnapshot[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            expected[index] = await ReadAsync(extractor, paths[index]);
            Assert.NotEmpty(expected[index].Segments);
            AssertUnchangedAndReleased(paths[index], originals[index]);
        }

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyWorkers = 0;
        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyWorkers) == workerCount) ready.SetResult(true);
            await start.Task;

            var results = new List<(int Index, ExtractionSnapshot Snapshot)>();
            for (var index = worker; index < paths.Length; index += workerCount)
                results.Add((index, await ReadAsync(extractor, paths[index])));
            return results;
        })).ToArray();

        // All workers reach the asynchronous gate before release. This exercises independent
        // calls on one instance, without claiming overlap inside synchronous third-party parsers.
        await ready.Task;
        start.SetResult(true);
        var completed = await Task.WhenAll(workers);
        var actual = completed.SelectMany(results => results).OrderBy(result => result.Index).ToArray();
        Assert.Equal(paths.Length, actual.Length);
        for (var index = 0; index < paths.Length; index++)
        {
            Assert.Equal(index, actual[index].Index);
            // Record equality includes every segment's Text, Id and the complete SourceLocator.
            Assert.Equal(expected[index].Segments, actual[index].Snapshot.Segments);
            Assert.Equal(expected[index].Warnings, actual[index].Snapshot.Warnings);
            AssertUnchangedAndReleased(paths[index], originals[index]);
        }
    }

    private static async Task<ExtractionSnapshot> ReadAsync(ITextExtractor extractor, string path)
    {
        var segments = new List<TextSegment>();
        var warnings = new List<string>();
        var context = new ExtractionContext(warnings.Add);
        await foreach (var segment in extractor.ExtractAsync(path, context)) segments.Add(segment);
        return new ExtractionSnapshot(segments.ToArray(), warnings.ToArray());
    }

    private static FileSnapshot Snapshot(string path)
    {
        using var stream = File.OpenRead(path);
        return new FileSnapshot(SHA256.HashData(stream), File.GetLastWriteTimeUtc(path), stream.Length);
    }

    private static void AssertUnchangedAndReleased(string path, FileSnapshot expected)
    {
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(expected.Length, exclusive.Length);
            Assert.Equal(expected.Hash, SHA256.HashData(exclusive));
        }
        Assert.Equal(expected.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
    }

    private sealed record ExtractionSnapshot(TextSegment[] Segments, string[] Warnings);
    private sealed record FileSnapshot(byte[] Hash, DateTime LastWriteTimeUtc, long Length);
}
