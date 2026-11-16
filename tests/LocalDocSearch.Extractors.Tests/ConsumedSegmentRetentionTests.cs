using System.Runtime.CompilerServices;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class ConsumedSegmentRetentionTests
{
    [Fact]
    public async Task PreviouslyConsumedText_CanBeCollectedWhileLaterSegmentsAreStillPending()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("owned.bin");
        File.WriteAllBytes(path, []);
        var references = new WeakReference<string>[12];
        await using var iterator = ReadOnlyExtraction.Run(path, new ExtractionContext(_ => { }),
            (_, _, _) => CreateSegments(references), default).GetAsyncEnumerator();
        for (int index = 0; index < 8; index++)
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("segment:" + index, iterator.Current.Id);
            Assert.Equal(index + 1, iterator.Current.Source.Page);
        }
        Collect();
        Assert.All(references.Take(7), reference => Assert.False(IsAlive(reference)));
        Assert.All(references.Skip(8), reference => Assert.True(IsAlive(reference)));
        for (int index = 8; index < references.Length; index++)
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("segment:" + index, iterator.Current.Id);
        }
        Assert.False(await iterator.MoveNextAsync());
    }

    [Fact]
    public async Task EarlyDispose_ReleasesPendingTextEvenIfTheDisposedEnumeratorIsKept()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("owned.bin");
        File.WriteAllBytes(path, []);
        var references = new WeakReference<string>[12];
        var iterator = ReadOnlyExtraction.Run(path, new ExtractionContext(_ => { }),
            (_, _, _) => CreateSegments(references), default).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        await iterator.DisposeAsync();
        Collect();
        Assert.All(references.Skip(1), reference => Assert.False(IsAlive(reference)));
        GC.KeepAlive(iterator);
    }

    [Fact]
    public async Task CancellationAfterFirstSegment_ReleasesPendingText()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("owned.bin");
        File.WriteAllBytes(path, []);
        using var cancellation = new CancellationTokenSource();
        var references = new WeakReference<string>[12];
        await using var iterator = ReadOnlyExtraction.Run(path, new ExtractionContext(_ => { }),
            (_, _, _) => CreateSegments(references), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        Collect();
        Assert.All(references.Skip(1), reference => Assert.False(IsAlive(reference)));
        GC.KeepAlive(iterator);
    }

    [Fact]
    public async Task LateParserFailure_StillPublishesNoSegments()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("owned.bin");
        File.WriteAllBytes(path, []);
        int published = 0;
        await Assert.ThrowsAsync<ExtractionException>(async () =>
        {
            await foreach (var segment in ReadOnlyExtraction.Run(path, new ExtractionContext(_ => { }),
                (_, _, _) =>
                {
                    _ = CreateSegments(new WeakReference<string>[12]);
                    throw new ExtractionException("late-invalid-part");
                }, default))
                published++;
        });
        Assert.Equal(0, published);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0, exclusive.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<TextSegment> CreateSegments(WeakReference<string>[] references)
    {
        var result = new List<TextSegment>(references.Length);
        for (int index = 0; index < references.Length; index++)
        {
            var text = new string((char)('a' + index), 128 * 1024);
            references[index] = new WeakReference<string>(text);
            result.Add(new TextSegment("segment:" + index, text, new SourceLocator(SourceKind.PdfPage, Page: index + 1)));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<string> reference) => reference.TryGetTarget(out _);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
