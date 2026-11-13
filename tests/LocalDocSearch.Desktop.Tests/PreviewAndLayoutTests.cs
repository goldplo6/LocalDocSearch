using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Extractors;
using LocalDocSearch.Fixtures;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class PreviewAndLayoutTests
{
    [Theory]
    [InlineData(".txt")][InlineData(".md")][InlineData(".log")][InlineData(".csv")]
    [InlineData(".docx")][InlineData(".xlsx")][InlineData(".pptx")][InlineData(".pdf")]
    public async Task EveryFormatReextractsAndSelectsTheExactOriginalOccurrence(string extension)
    {
        using var scope = new TempFiles();
        SampleDocuments.CreateAll(scope.Root);
        var files = new List<FileResult>();
        await new TextSearchEngine(DocumentExtractorCatalog.CreateDefault()).SearchAsync(
            new(scope.Root, ["西瓜"], Extensions: [extension]), null, new Inline<FileResult>(files.Add), cancellationToken: TestContext.Current.CancellationToken);
        var file = Assert.Single(files);
        foreach (var match in file.Matches)
        {
            var data = await new MatchPreviewLoader().LoadAsync(Request(file.FullPath, match), TestContext.Current.CancellationToken);
            Assert.Equal(match.Text, data.Text.Substring(data.SelectionStart, data.SelectionLength));
        }
    }

    [Fact]
    public async Task DuplicateWordsKeepTheRequestedOffsetAndPreviewIsBounded()
    {
        using var scope = new TempFiles();
        var text = "西瓜" + new string('a', 40000) + "西瓜末尾";
        var path = scope.Write(text);
        var match = new MatchItem("西瓜", "aaa", "西瓜", "末尾", new SourceLocation(1, 40003), "line:1", 40002);
        var loader = new MatchPreviewLoader([new FakeExtractor(new("line:1", text, new SourceLocation(1, 1)))]);
        var data = await loader.LoadAsync(Request(path, match), TestContext.Current.CancellationToken);
        Assert.True(data.IsClipped); Assert.InRange(data.Text.Length, 2, MatchPreviewLoader.MaximumPreviewCharacters);
        Assert.Equal("西瓜", data.Text.Substring(data.SelectionStart, data.SelectionLength));
        Assert.Equal("末尾", data.Text[(data.SelectionStart + data.SelectionLength)..]);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ChangedSourceNeverSelectsAnotherOccurrence(bool changeTimestamp)
    {
        using var scope = new TempFiles();
        var path = scope.Write("甲西瓜乙");
        var stamp = File.GetLastWriteTimeUtc(path);
        var match = new MatchItem("西瓜", "甲", "西瓜", "乙", new SourceLocation(1, 2), "line:1", 1);
        var request = Request(path, match);
        File.WriteAllText(path, "丙西瓜丁");
        File.SetLastWriteTimeUtc(path, changeTimestamp ? stamp.AddSeconds(2) : stamp);
        await Assert.ThrowsAsync<IOException>(() => new MatchPreviewLoader().LoadAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LateCorruptionAndCancellationDoNotPublishAnEarlyTarget()
    {
        using var scope = new TempFiles();
        var path = scope.Write("西瓜");
        var match = new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(1, 1), "line:1");
        var loader = new MatchPreviewLoader([new FakeExtractor(new("line:1", "西瓜", new SourceLocation(1, 1)), failAfterTarget: true)]);
        await Assert.ThrowsAsync<ExtractionException>(() => loader.LoadAsync(Request(path, match), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MatchPreviewLoader().LoadAsync(Request(path, match), cancellation.Token));
    }

    [AvaloniaTheory]
    [InlineData(850, 640)][InlineData(1140, 820)][InlineData(1600, 900)]
    public void ResultsOccupyAtLeastSixtyPercentWithManyKeywords(double width, double height)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 64; i++) vm.Terms.Add(new string('词', 100) + i);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var list = window.FindControl<ListBox>("ResultsList")!;
            var ratio = list.Bounds.Width * list.Bounds.Height / (window.ClientSize.Width * window.ClientSize.Height);
            Assert.True(ratio >= .60, $"Result area ratio {ratio:P2} at {width}×{height}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PreviewTextBoxActuallySelectsTheSecondRepeatedWordAndRemainsReadOnly()
    {
        using var scope = new TempFiles();
        var path = scope.Write("第一处西瓜，第二处西瓜END");
        var match = new MatchItem("西瓜", "第二处", "西瓜", "END", new SourceLocation(1, 10), "line:1", 9);
        var window = new MatchPreviewWindow(Request(path, match));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await window.SelectionTask;
            var box = Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
            Assert.True(window.SelectionVerified); Assert.True(box.IsReadOnly);
            Assert.Equal("西瓜", box.SelectedText); Assert.Equal(9, box.SelectionStart);
            Assert.Equal("第二处", box.Text!.Substring(box.SelectionStart - 3, 3));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PreviewUsesTheSearchEncodingSnapshotAfterSettingsChange()
    {
        using var scope = new TempFiles();
        EncodingSamples.CreateAll(scope.Root);
        var vm = new MainWindowViewModel { RootFolder = scope.Root, PendingTerm = "西瓜", EncodingIndex = 1 };
        await vm.StartSearchCommand.ExecuteAsync(null);
        var file = vm.Results.Single(f => f.FullPath.EndsWith("GB18030原始.csv"));
        vm.ApplySettings(vm.CaptureSettings() with { EncodingMode = TextEncodingMode.StrictUnicode });
        MatchPreviewRequest? request = null; vm.PreviewRequested += r => request = r;
        vm.PreviewFileCommand.Execute(file);
        Assert.NotNull(request); Assert.Equal(TextEncodingMode.Utf8OrGb18030, request.EncodingMode);
        var data = await new MatchPreviewLoader().LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("西瓜", data.Text.Substring(data.SelectionStart, data.SelectionLength));
    }

    [Fact]
    public async Task PageSourceMismatchNeverClaimsTheWrongPage()
    {
        using var scope = new TempFiles();
        var path = scope.Write("西瓜");
        var actual = new SourceLocator(SourceKind.PdfPage, Page: 200);
        var match = new MatchItem("西瓜", "", "西瓜", "", actual with { Page = 150 }, "pdf:p200");
        var loader = new MatchPreviewLoader([new FakeExtractor(new("pdf:p200", "西瓜", actual))]);
        await Assert.ThrowsAsync<IOException>(() => loader.LoadAsync(Request(path, match), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LongDocumentSecondOccurrencesKeepTheirRealLateSources()
    {
        using var scope = new TempFiles();
        LongDocuments.CreateAll(scope.Root);
        var files = new List<FileResult>();
        var summary = await new TextSearchEngine(DocumentExtractorCatalog.CreateDefault()).SearchAsync(
            new(scope.Root, [LongDocuments.RepeatedTerm]), null, new Inline<FileResult>(files.Add),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal(0, summary.Diagnostics.ErrorCount);
        Assert.Equal(4, files.Count); Assert.Equal(8, files.Sum(f => f.TotalOccurrences));
        foreach (var file in files)
        {
            var match = file.Matches.Last();
            var data = await new MatchPreviewLoader().LoadAsync(Request(file.FullPath, match), TestContext.Current.CancellationToken);
            Assert.Equal(LongDocuments.RepeatedTerm, data.Text.Substring(data.SelectionStart, data.SelectionLength));
            switch (file.Extension)
            {
                case ".pdf": Assert.Equal(200, match.Location.Page); Assert.Contains("200", data.Position); break;
                case ".docx": Assert.Equal("word:p2398", match.SourceSegmentId); break;
                case ".txt": Assert.Equal(100000, match.Location.Line); break;
                case ".csv": Assert.Equal(20001, match.Location.Record); Assert.Equal(3, match.Location.Field); break;
                default: throw new InvalidOperationException("Unexpected generated format.");
            }
        }
    }

    private static MatchPreviewRequest Request(string path, MatchItem match) =>
        new(path, match, File.GetLastWriteTimeUtc(path), TextEncodingMode.StrictUnicode, new());
    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class FakeExtractor(TextSegment segment, bool failAfterTarget = false) : ITextExtractor
    {
        public IReadOnlyCollection<string> Extensions => [".txt"];
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return segment;
            if (failAfterTarget) throw new ExtractionException("late corrupt data");
        }
    }
    private sealed class TempFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-preview-" + Guid.NewGuid().ToString("N"));
        public TempFiles() => Directory.CreateDirectory(Root);
        public string Write(string text) { var path = Path.Combine(Root, "sample.txt"); File.WriteAllText(path, text); return path; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
