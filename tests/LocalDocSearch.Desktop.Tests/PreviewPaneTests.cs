using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.Controls;
using LocalDocSearch.Extractors;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class PreviewPaneTests
{
    [AvaloniaFact]
    public async Task SidebarSelectsRequestedRepeatedOccurrenceAndStaysReadOnly()
    {
        using var files = new SourceFiles();
        var request = files.Request("第一处西瓜，第二处西瓜END", "西瓜", 9, "第二处", "END");
        using var pane = new MatchPreviewPane();
        var window = new Window { Content = pane, Width = 340, Height = 640 };
        try
        {
            window.Show();
            await pane.ShowAsync(request);
            var box = Assert.Single(pane.GetVisualDescendants().OfType<TextBox>());
            Assert.True(pane.SelectionVerified);
            Assert.True(box.IsReadOnly);
            Assert.Equal("西瓜", box.SelectedText);
            Assert.Equal(9, box.SelectionStart);
            Assert.Contains("行 1", pane.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("已选中", StringComparison.Ordinal) == true).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReplacingPreviewDiscardsLateCompletionFromPreviousRequest()
    {
        using var files = new SourceFiles();
        var first = files.Request("甲西瓜乙", "西瓜", 1, "甲", "乙");
        var second = files.Request("丙苹果丁", "苹果", 1, "丙", "丁");
        var blocked = new BlockedExtractor("甲西瓜乙");
        using var pane = new MatchPreviewPane();
        var window = new Window { Content = pane };
        Task? previous = null;
        try
        {
            window.Show();
            previous = pane.ShowAsync(first, new MatchPreviewLoader([blocked]));
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var replacement = pane.ShowAsync(second);
            blocked.Release.TrySetResult();
            await previous;
            await replacement;
            Dispatcher.UIThread.RunJobs();
            var box = Assert.Single(pane.GetVisualDescendants().OfType<TextBox>());
            Assert.True(pane.SelectionVerified);
            Assert.Equal("苹果", box.SelectedText);
            Assert.Equal("丙苹果丁", box.Text);
        }
        finally
        {
            blocked.Release.TrySetResult();
            if (previous is not null) await previous;
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearOrDisposeDuringLoadLeavesNoTextOrVerifiedSelection(bool dispose)
    {
        using var files = new SourceFiles();
        var request = files.Request("甲西瓜乙", "西瓜", 1, "甲", "乙");
        var blocked = new BlockedExtractor("甲西瓜乙");
        using var pane = new MatchPreviewPane();
        var window = new Window { Content = pane };
        Task? loading = null;
        try
        {
            window.Show();
            loading = pane.ShowAsync(request, new MatchPreviewLoader([blocked]));
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (dispose) pane.Dispose(); else pane.Clear();
            blocked.Release.TrySetResult();
            await loading;
            Dispatcher.UIThread.RunJobs();
            var box = Assert.Single(pane.GetVisualDescendants().OfType<TextBox>());
            Assert.False(pane.SelectionVerified);
            Assert.True(string.IsNullOrEmpty(box.Text));
            Assert.DoesNotContain(pane.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("已选中", StringComparison.Ordinal) == true);
            if (dispose) Assert.Throws<ObjectDisposedException>(() => { _ = pane.ShowAsync(request); });
            else { await pane.ShowAsync(request); Assert.True(pane.SelectionVerified); }
        }
        finally
        {
            blocked.Release.TrySetResult();
            if (loading is not null) await loading;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangedSourceClearsPreviousSelectionAndReportsFailure()
    {
        using var files = new SourceFiles();
        var request = files.Request("甲西瓜乙", "西瓜", 1, "甲", "乙");
        using var pane = new MatchPreviewPane();
        var window = new Window { Content = pane };
        try
        {
            window.Show();
            await pane.ShowAsync(request);
            Assert.True(pane.SelectionVerified);
            File.SetLastWriteTimeUtc(request.FullPath, request.LastWriteTimeUtc.AddSeconds(5));
            await pane.ShowAsync(request);
            Assert.False(pane.SelectionVerified);
            Assert.True(string.IsNullOrEmpty(Assert.Single(pane.GetVisualDescendants().OfType<TextBox>()).Text));
            Assert.Contains(pane.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("定位未完成：", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    private sealed class BlockedExtractor(string text) : ITextExtractor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> Extensions => [".txt"];
        public async IAsyncEnumerable<TextSegment> ExtractAsync(string path, ExtractionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task;
            // Deliberately complete late even after cancellation; the pane must discard it.
            yield return new TextSegment("line:1", text, new SourceLocation(1, 1));
        }
    }

    private sealed class SourceFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalDocSearch-pane-" + Guid.NewGuid().ToString("N"));
        public SourceFiles() => Directory.CreateDirectory(_root);
        public MatchPreviewRequest Request(string text, string term, int offset, string before, string after)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, text);
            return new(path, new MatchItem(term, before, term, after, new SourceLocation(1, offset + 1), "line:1", offset),
                File.GetLastWriteTimeUtc(path), TextEncodingMode.StrictUnicode, new DocumentScopeOptions());
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
