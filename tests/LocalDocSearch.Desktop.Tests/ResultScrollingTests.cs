using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class ResultScrollingTests(ITestOutputHelper output)
{
    [AvaloniaTheory]
    [InlineData(1, 850, 640)]
    [InlineData(300, 850, 640)]
    [InlineData(10_000, 1140, 820)]
    public void LastFileWheelReachesLastLoadedMatchAndLoadMoreBottom(int fileCount, double width, double height)
    {
        var vm = CreateResults(fileCount);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            window.Show();
            Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            list.ScrollIntoView(fileCount - 1);
            Settle(window);
            var expander = FileExpander(list, fileCount - 1);
            Assert.False(vm.Results[^1].IsExpanded);
            Assert.All(vm.Results, file => Assert.Empty(file.VisibleMatches));
            expander.SetCurrentValue(Expander.IsExpandedProperty, true);
            Settle(window);
            Assert.Equal(50, vm.Results[^1].VisibleMatches.Count);
            output.WriteLine($"Expanded last file: {Describe(list)}; height={expander.Bounds.Height}");
            WheelToLoadedBottom(window, list, vm.Results[^1], fileCount - 1);

            ClickLoadMore(window, list, fileCount - 1);
            Assert.Equal(100, vm.Results[^1].VisibleMatches.Count);
            WheelToLoadedBottom(window, list, vm.Results[^1], fileCount - 1);
            ClickLoadMore(window, list, fileCount - 1);
            Assert.Equal(123, vm.Results[^1].VisibleMatches.Count);
            WheelToLoadedBottom(window, list, vm.Results[^1], fileCount - 1);
            var finishedButton = LoadMoreButton(list, fileCount - 1);
            Assert.True(finishedButton.IsVisible);
            Assert.False(finishedButton.IsEnabled);
            Assert.InRange(list.GetRealizedContainers().Count(), 1, 30);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycledFileReturnsExpandedWithoutExpandingAnotherFileAndStillReachesMatchBottom(bool loadMoreBeforeRecycle)
    {
        var vm = CreateResults(500);
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 640 };
        try
        {
            window.Show();
            Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            list.ScrollIntoView(499);
            Settle(window);
            var original = FileExpander(list, 499);
            original.SetCurrentValue(Expander.IsExpandedProperty, true);
            Settle(window);
            Assert.Equal(50, vm.Results[^1].VisibleMatches.Count);
            WheelToLoadedBottom(window, list, vm.Results[^1], 499);
            if (loadMoreBeforeRecycle)
            {
                ClickLoadMore(window, list, 499);
                Assert.Equal(100, vm.Results[^1].VisibleMatches.Count);
                WheelToLoadedBottom(window, list, vm.Results[^1], 499);
            }
            var loaded = vm.Results[^1].VisibleMatches.ToArray();

            list.ScrollIntoView(0);
            Settle(window);
            Assert.Null(list.ContainerFromIndex(499));
            foreach (var container in list.GetRealizedContainers())
            {
                var other = Assert.Single(container.GetVisualDescendants().OfType<Expander>());
                Assert.False(other.IsExpanded, $"Expanded state leaked to {other.DataContext}");
            }
            list.ScrollIntoView(499);
            Settle(window);
            var returned = FileExpander(list, 499);
            Assert.True(returned.IsExpanded, "The last file collapsed after its container was recycled.");
            Assert.Equal(loadMoreBeforeRecycle ? 100 : 50, vm.Results[^1].VisibleMatches.Count);
            for (var i = 0; i < loaded.Length; i++) Assert.Same(loaded[i], vm.Results[^1].VisibleMatches[i]);
            Assert.All(vm.Results.Take(499), file => Assert.Empty(file.VisibleMatches));
            WheelToLoadedBottom(window, list, vm.Results[^1], 499);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgrammaticExpansionBeforeOrAfterRealizationLoadsOnlyOnePage(bool expandBeforeRealization)
    {
        var vm = CreateResults(1);
        var file = vm.Results[0];
        if (expandBeforeRealization) file.IsExpanded = true;
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 640 };
        try
        {
            window.Show();
            Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            if (!expandBeforeRealization)
            {
                Assert.Empty(file.VisibleMatches);
                file.IsExpanded = true;
                Settle(window);
            }
            var expander = FileExpander(list, 0);
            Assert.True(expander.IsExpanded);
            Assert.Equal(50, file.VisibleMatches.Count);
            var loaded = file.VisibleMatches.ToArray();
            file.IsExpanded = false;
            Settle(window);
            Assert.False(expander.IsExpanded);
            file.IsExpanded = true;
            Settle(window);
            Assert.True(expander.IsExpanded);
            Assert.Equal(50, file.VisibleMatches.Count);
            for (var i = 0; i < loaded.Length; i++) Assert.Same(loaded[i], file.VisibleMatches[i]);
            WheelToLoadedBottom(window, list, file, 0);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledOrRemovedPendingExpansionDoesNotPullViewportBack(bool removeFile)
    {
        var vm = CreateResults(300);
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 640 };
        try
        {
            window.Show();
            Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            list.ScrollIntoView(299);
            Settle(window);
            var file = vm.Results[^1];
            var expander = FileExpander(list, 299);
            expander.SetCurrentValue(Expander.IsExpandedProperty, true);
            // Complete the user's next action before the deferred layout callback runs.
            if (removeFile) vm.Results.Remove(file);
            else expander.SetCurrentValue(Expander.IsExpandedProperty, false);
            list.ScrollIntoView(0);
            Settle(window);
            Assert.NotNull(list.ContainerFromIndex(0));
            if (!removeFile) Assert.False(file.IsExpanded);
            Assert.InRange(list.GetRealizedContainers().Count(), 1, 30);
        }
        finally { window.Close(); }
    }

    private void WheelToLoadedBottom(MainWindow window, ListBox results, FileResultViewModel file, int lastIndex)
    {
        // Only raw pointer-wheel input advances either ScrollViewer. Never ScrollToEnd or
        // ScrollIntoView a match: the test must prove the user can reach the actual content.
        for (var tick = 0; tick < 450; tick++)
        {
            var expander = FileExpander(results, lastIndex);
            Assert.True(expander.IsExpanded, $"Collapsed after wheel tick {tick}: {Describe(results)}");
            var matches = Assert.Single(expander.GetVisualDescendants().OfType<ListBox>());
            var inner = Viewer(matches);
            var outer = Viewer(results);
            if (AtBottom(inner) && AtBottom(outer))
            {
                var last = matches.ContainerFromIndex(file.VisibleMatches.Count - 1);
                if (last is not null && FullyVisible(last, inner, window) && FullyVisible(matches, outer, window))
                {
                    output.WriteLine($"Reached {file.VisibleMatches.Count}: ticks={tick}; outer={Describe(results)}; inner={Describe(matches)}");
                    // Wheel through both boundaries repeatedly; staying expanded is part of the fix.
                    for (var repeat = 0; repeat < 8; repeat++) Wheel(window, VisibleCenter(matches, outer, window), -1);
                    Assert.True(FileExpander(results, lastIndex).IsExpanded);
                    Assert.True(FullyVisible(last, inner, window));
                    return;
                }
            }
            var point = VisibleCenter(matches, outer, window);
            Wheel(window, point, -1);
        }
        var current = FileExpander(results, lastIndex);
        var details = Assert.Single(current.GetVisualDescendants().OfType<ListBox>());
        Assert.Fail($"Wheel did not reach loaded match {file.VisibleMatches.Count}. outer={Describe(results)}; inner={Describe(details)}; lastContainer={details.ContainerFromIndex(file.VisibleMatches.Count - 1)?.Bounds}; details={BoundsInWindow(details, window)}");
    }

    private static Button LoadMoreButton(ListBox results, int index) =>
        FileExpander(results, index).GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "再显示 50 处"));

    private static void ClickLoadMore(MainWindow window, ListBox results, int index)
    {
        var button = LoadMoreButton(results, index);
        var outer = Viewer(results);
        Assert.True(FullyVisible(button, outer, window), "Load-more must be reachable inside the result viewport.");
        Assert.True(button.IsEnabled);
        var point = BoundsInWindow(button, window).Center;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static MainWindowViewModel CreateResults(int count)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < count; i++)
        {
            var matches = Enumerable.Range(1, i == count - 1 ? 123 : 1)
                .Select(n => new MatchItem("命中", new string('前', n % 4 * 25), "命中", $"TAIL-{n:D3}-" + new string('后', n % 3 * 18), new SourceLocation(n, 1)))
                .ToArray();
            vm.Results.Add(new FileResultViewModel(new FileResult($"C:/fixtures/file-{i:D5}.txt", ".txt", DateTime.UtcNow, matches)));
        }
        return vm;
    }

    private static Expander FileExpander(ListBox list, int index) =>
        Assert.Single(Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(index)).GetVisualDescendants().OfType<Expander>());

    private static ScrollViewer Viewer(ListBox list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static bool AtBottom(ScrollViewer viewer) => viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y < 1;

    private static Rect BoundsInWindow(Visual visual, MainWindow window) =>
        new(visual.TranslatePoint(default, window)!.Value, visual.Bounds.Size);

    private static bool FullyVisible(Visual visual, ScrollViewer viewer, MainWindow window)
    {
        var bounds = BoundsInWindow(visual, window);
        var view = BoundsInWindow(viewer, window);
        return bounds.Height > 0 && bounds.Top >= view.Top - 1 && bounds.Bottom <= view.Bottom + 1;
    }

    private static Point VisibleCenter(Control control, ScrollViewer outer, MainWindow window)
    {
        var visible = BoundsInWindow(control, window).Intersect(BoundsInWindow(outer, window));
        Assert.True(visible.Height > 0, $"Match list is outside the outer viewport: {visible}");
        return visible.Center;
    }

    private static void Wheel(MainWindow window, Point point, double delta)
    {
        window.MouseMove(point);
        window.MouseWheel(point, new Vector(0, delta));
        Settle(window);
    }

    private static void Settle(MainWindow window)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Describe(ListBox list)
    {
        var viewer = Viewer(list);
        var panel = Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        return $"offset={viewer.Offset.Y:F1},extent={viewer.Extent.Height:F1},viewport={viewer.Viewport.Height:F1},realized={panel.FirstRealizedIndex}..{panel.LastRealizedIndex}";
    }
}
