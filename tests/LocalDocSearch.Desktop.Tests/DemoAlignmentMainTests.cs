using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.Controls;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class DemoAlignmentMainTests
{
    [AvaloniaTheory]
    [InlineData(850, 640, 1d)]
    [InlineData(850, 640, 1.25d)]
    [InlineData(850, 640, 2d)]
    [InlineData(1140, 820, 1d)]
    [InlineData(1600, 900, 1.5d)]
    public void BrandSearchAndResultsAreSeparateRegionsWithSixtyPercentResultViewport(double width, double height, double scale)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 64; i++) vm.Terms.Add(new string('词', 100) + i);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            window.Show(); Settle(window);
            window.SetRenderScaling(scale); Settle(window);
            Assert.Equal(scale, window.RenderScaling);
            var header = window.FindControl<Border>("BrandHeader")!;
            var search = window.FindControl<Border>("SearchCard")!;
            var toolbar = window.FindControl<Grid>("ResultsToolbar")!;
            var list = window.FindControl<ListBox>("ResultsList")!;
            Assert.NotNull(window.Icon);
            var mark = Assert.Single(header.GetVisualDescendants().OfType<BrandMark>());
            var image = Assert.Single(mark.GetVisualDescendants().OfType<Image>());
            Assert.IsType<Avalonia.Media.Imaging.Bitmap>(image.Source);
            using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://DocSearch/Assets/app-icon.png"));
            var pngHeader = new byte[24];
            iconStream.ReadExactly(pngHeader);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, pngHeader[..8]);
            Assert.Equal(13, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pngHeader.AsSpan(8, 4)));
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(pngHeader, 12, 4));
            Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pngHeader.AsSpan(16, 4)));
            Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pngHeader.AsSpan(20, 4)));
            Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(header.Background).Color);
            Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "离线 · 只读");
            Assert.InRange(Math.Abs(header.Bounds.Width - window.ClientSize.Width), 0d, 1d / window.RenderScaling);
            Assert.True(Bounds(header, window).Bottom <= Bounds(search, window).Top);
            Assert.True(Bounds(search, window).Bottom <= Bounds(toolbar, window).Top);
            Assert.True(Bounds(toolbar, window).Bottom <= Bounds(list, window).Top);
            Assert.True(list.Bounds.Width * list.Bounds.Height / (window.ClientSize.Width * window.ClientSize.Height) >= .60);
            var formula = Assert.Single(search.GetVisualDescendants().OfType<TextBlock>(),
                text => AutomationProperties.GetAutomationId(text) == "SearchQueryFormula");
            Assert.True(formula.IsEffectivelyVisible);
            Assert.True(Bounds(search, window).Contains(Bounds(formula, window)));
            var count = Assert.Single(toolbar.GetVisualDescendants().OfType<TextBlock>(),
                text => AutomationProperties.GetAutomationId(text) == "ResultFileCount");
            Assert.Equal("0 个文件", count.Text);
            vm.Results.Add(CreateFile("report.pdf")); Settle(window);
            Assert.Equal("1 个文件", count.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FileTypesHaveDistinctBadgesAndOneChevronExpandsOnlyItsOwnRealExpander()
    {
        var vm = new MainWindowViewModel();
        vm.Results.Add(CreateFile("report.pdf", 123));
        vm.Results.Add(CreateFile("notes.docx"));
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            var first = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            var second = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(1));
            var pdf = FindBadge(first);
            var word = FindBadge(second);
            Assert.Contains(pdf.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "PDF");
            Assert.Contains(word.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "DOCX");
            Assert.NotEqual(Assert.IsAssignableFrom<ISolidColorBrush>(pdf.Background).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(word.Background).Color);
            Assert.Equal(vm.Results[0].Title, AutomationProperties.GetName(first));
            var expander = Assert.Single(first.GetVisualDescendants().OfType<Expander>());
            var chevron = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>());
            var point = chevron.TranslatePoint(new Point(chevron.Bounds.Width / 2, chevron.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Settle(window);
            Assert.True(expander.IsExpanded);
            Assert.True(vm.Results[0].IsExpanded);
            Assert.False(vm.Results[1].IsExpanded);
            var matches = Assert.Single(expander.GetVisualDescendants().OfType<ListBox>());
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            Assert.IsType<VirtualizingStackPanel>(matches.ItemsPanelRoot);
            var innerScroll = Assert.Single(matches.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(innerScroll.Extent.Height > innerScroll.Viewport.Height);
            var initialOffset = innerScroll.Offset.Y;
            var wheelPoint = matches.TranslatePoint(new Point(matches.Bounds.Width / 2, matches.Bounds.Height / 2), window)!.Value;
            window.MouseMove(wheelPoint); window.MouseWheel(wheelPoint, new Vector(0, -6));
            Settle(window);
            Assert.True(innerScroll.Offset.Y > initialOffset);
            var preview = Assert.Single(first.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.Command, vm.PreviewFileCommand));
            Assert.Equal("定位并选中", AutomationProperties.GetName(preview));
            Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "预览");
        }
        finally { window.Close(); }
    }

    private static FileResultViewModel CreateFile(string name, int count = 1) => new(new FileResult(
        Path.Combine(@"C:\fixtures", name), Path.GetExtension(name), new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
        Enumerable.Range(0, count).Select(index => new MatchItem("命中", "前文", "命中", "后文",
            new SourceLocation(index + 1, 2))).ToArray()));

    private static Border FindBadge(Control row) => Assert.Single(row.GetVisualDescendants().OfType<Border>(),
        badge => AutomationProperties.GetAutomationId(badge) == "FileTypeBadge");

    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
