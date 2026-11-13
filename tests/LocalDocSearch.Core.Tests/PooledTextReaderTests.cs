using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class PooledTextReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8187)]
    [InlineData(8188)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(16384)]
    public async Task PhysicalLinesMatchReferenceAtBufferBoundaries(int prefixLength)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch.PooledReader.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "original.txt");
            var text = new string('甲', prefixLength) + "西瓜🍉\r\n\r" +
                new string('乙', 8193) + "苹果\n末尾\r\n未换行🍉";
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            // A small, known test input uses the platform line reader as an independent oracle.
            var expected = await File.ReadAllLinesAsync(path);
            var context = new ExtractionContext(_ => { }, limits: new SearchResourceLimits
            {
                MaxSegmentCharacters = expected.Max(line => line.Length),
            });
            var segments = new List<TextSegment>();
            await foreach (var segment in new PlainTextExtractor().ExtractAsync(path, context))
                segments.Add(segment);
            Assert.Equal(expected, segments.Select(segment => segment.Text));
            for (var index = 0; index < segments.Count; index++)
            {
                Assert.Equal($"line:{index + 1}", segments[index].Id);
                Assert.Equal(index + 1, segments[index].Source.Line);
                Assert.Equal(1, segments[index].Source.Column);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
