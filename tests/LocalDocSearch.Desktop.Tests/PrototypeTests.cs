using System.Threading.Channels;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.Controls;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class PrototypeTests
{
    [AvaloniaFact]
    public void MainWindowLoadsAndTenThousandResultsUseVirtualization()
    {
        var vm = new MainWindowViewModel();
        vm.LoadDemoCommand.Execute(null);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var list = window.FindControl<ListBox>("ResultsList")!;
            Assert.Equal(10_000, vm.Results.Count);
            Assert.Equal(10_000, list.ItemCount);
            var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Assert.InRange(realized, 1, 199);
            list.ScrollIntoView(9_999);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.NotNull(list.ContainerFromIndex(9_999));
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 199);
            Assert.Contains("模拟", vm.StatusText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HighlightUsesBothBoldMarkerAndSourceContext()
    {
        var text = new ExcerptTextBlock { Before = "我喜欢", Match = "西瓜", After = "汁" };
        var runs = text.Inlines!.OfType<Run>().ToArray();
        Assert.Equal(3, runs.Length);
        Assert.Equal("我喜欢", runs[0].Text);
        Assert.Equal("【西瓜】", runs[1].Text);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, runs[1].FontWeight);
        Assert.Equal("汁", runs[2].Text);
    }

    [AvaloniaFact]
    public void MatchDetailsLoadInPagesWithoutChangingTotal()
    {
        var matches = Enumerable.Range(1, 123)
            .Select(i => new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(i, 1))).ToArray();
        var vm = new FileResultViewModel(new FileResult("demo.txt", ".txt", DateTime.UtcNow, matches));
        Assert.Equal(50, vm.VisibleMatches.Count);
        Assert.Equal(123, vm.TotalOccurrences);
        Assert.True(vm.HasMore);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(100, vm.VisibleMatches.Count);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(123, vm.VisibleMatches.Count);
        Assert.False(vm.HasMore);
    }

    [AvaloniaFact]
    public async Task CancellingRetainsAllAlreadyReceivedErrorsAcrossBatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "sample.txt"), "西瓜");
            var vm = new MainWindowViewModel { RootFolder = directory, PendingTerm = "西瓜" };
            Channel<SearchIssue>? channel = null;
            // Seed a full, already accepted backlog after the per-query channel exists.
            // Its 256 entries need several UI batches; one additional entry must wait.
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(vm.IsBusy) || !vm.IsBusy) return;
                channel = Assert.IsAssignableFrom<Channel<SearchIssue>>(typeof(MainWindowViewModel)
                    .GetField("_pendingIssues", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
                for (var i = 0; i < 256; i++)
                    Assert.True(channel.Writer.TryWrite(new SearchIssue($"fixture-{i}.txt", "读取错误夹具")));
                Assert.False(channel.Writer.TryWrite(new SearchIssue("overflow.txt", "未接收的额外详情")));
            };
            var countAtCancellation = -1;
            vm.Issues.CollectionChanged += (_, _) =>
            {
                if (vm.Issues.Count == 0 || countAtCancellation >= 0) return;
                countAtCancellation = vm.Results.Count;
                vm.CancelSearchCommand.Execute(null);
            };
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.Equal(256, vm.Issues.Count);
            Assert.NotNull(channel);
            Assert.Equal(0, channel.Reader.Count);
            Assert.Equal(countAtCancellation, vm.Results.Count);
            Assert.Contains("已停止", vm.StatusText);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ViewModelSearchesAcrossLinesAndReportsInvalidInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "中文 示例.txt"), "第一行西瓜\n第二行苹果\n");
            var vm = new MainWindowViewModel { RootFolder = directory, PendingTerm = "西瓜" };
            vm.AddTermCommand.Execute(null);
            vm.PendingTerm = "苹果";
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.Single(vm.Results);
            Assert.Equal(2, vm.Results[0].TotalOccurrences);
            Assert.Equal(2, vm.Terms.Count);
            Assert.Contains("已完成", vm.StatusText);
            Assert.Empty(vm.Issues);
            vm.IncludeTxt = vm.IncludeMd = vm.IncludeLog = vm.IncludeCsv = vm.IncludeDocx = vm.IncludeXlsx = vm.IncludePptx = vm.IncludePdf = false;
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.Contains("文件类型", vm.StatusText);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
