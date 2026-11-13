using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.Controls;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Navigation;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class DesignMainWindowTests
{
    [AvaloniaTheory]
    [InlineData(850, 640)]
    [InlineData(1140, 820)]
    [InlineData(1600, 900)]
    public void CompactSearchCardKeepsSixtyPercentResultsAndOnePrimaryAction(double width, double height)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 64; i++) vm.Terms.Add(new string('词', 100) + i);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            window.Show(); Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            Assert.False(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            Assert.True(list.Bounds.Width * list.Bounds.Height / (window.ClientSize.Width * window.ClientSize.Height) >= .60);
            var primary = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("primary"));
            Assert.Equal("开始搜索", primary.Content);
            Assert.Equal(Color.Parse("#F4F6FA"), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
            AssertInside(window, window.FindControl<TextBox>("KeywordBox")!);
            AssertInside(window, window.FindControl<Button>("GroupingButton")!);
            AssertInside(window, primary);
            AssertInside(window, list);
            Assert.Equal(64, vm.Terms.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FileHeaderSeparatesNameCountPathAndTimeAndMovesOriginalFindIntoMenu()
    {
        using var fixture = new PreviewFixture();
        var vm = fixture.ViewModel;
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            var row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains(fixture.File.FileName, texts);
            Assert.Contains(fixture.File.MatchCountText, texts);
            Assert.Contains(fixture.File.FullPath, texts);
            Assert.Contains(fixture.File.ModifiedText, texts);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "FindOriginalFile");
            Assert.Contains(row.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "定位并选中"));
            Assert.Equal(fixture.File.Title, AutomationProperties.GetName(row));
            Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WidePreviewUsesVerifiedInlineSelectionAndCloseRestoresFullListWidth()
    {
        using var fixture = new PreviewFixture();
        var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1140 };
        try
        {
            window.Show(); Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            var fullWidth = list.Bounds.Width;
            var pane = window.FindControl<MatchPreviewPane>("InlinePreviewPane")!;
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File);
            Settle(window); await pane.SelectionTask; Settle(window);
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            Assert.InRange(fullWidth - list.Bounds.Width, 340, 350);
            Assert.True(pane.SelectionVerified);
            Assert.Equal("西瓜", pane.GetVisualDescendants().OfType<TextBox>().Single().SelectedText);
            FindButton(pane, "CloseInlinePreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.False(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            Assert.Equal(fullWidth, list.Bounds.Width);
            Assert.False(pane.SelectionVerified);
            Assert.True(string.IsNullOrEmpty(pane.GetVisualDescendants().OfType<TextBox>().Single().Text));
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File);
            await pane.SelectionTask;
            Assert.True(pane.SelectionVerified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NarrowingTransfersCurrentRequestToOwnedWindowAndPopOutUsesTheSamePath()
    {
        using var fixture = new PreviewFixture();
        var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1140 };
        try
        {
            window.Show(); Settle(window);
            var pane = window.FindControl<MatchPreviewPane>("InlinePreviewPane")!;
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File);
            await pane.SelectionTask;
            window.Width = 850; Settle(window);
            Assert.False(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            Assert.False(pane.SelectionVerified);
            var standalone = Assert.IsType<MatchPreviewWindow>(Assert.Single(window.OwnedWindows));
            await standalone.SelectionTask;
            Assert.True(standalone.SelectionVerified);
            Assert.Equal("西瓜", standalone.GetVisualDescendants().OfType<TextBox>().Single().SelectedText);
            standalone.Close();

            // Requests made while narrow continue through the existing standalone path.
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File); Settle(window);
            standalone = Assert.IsType<MatchPreviewWindow>(Assert.Single(window.OwnedWindows));
            await standalone.SelectionTask;
            Assert.True(standalone.SelectionVerified);
            standalone.Close();

            window.Width = 1140; Settle(window);
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File);
            await pane.SelectionTask;
            FindButton(pane, "PopOutPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.False(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            standalone = Assert.IsType<MatchPreviewWindow>(Assert.Single(window.OwnedWindows));
            await standalone.SelectionTask;
            Assert.True(standalone.SelectionVerified);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ContextChangeAndCloseClearPendingPreviewAndDetachOldViewModel()
    {
        using var fixture = new PreviewFixture();
        var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1140 };
        var replacement = new MainWindowViewModel();
        try
        {
            window.Show(); Settle(window);
            var pane = window.FindControl<MatchPreviewPane>("InlinePreviewPane")!;
            // The host can hide before its first template pass; inspect the actual constructed child.
            var previewText = Assert.Single(Assert.IsType<Grid>(pane.Content).Children.OfType<TextBox>());
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File);
            var pending = pane.SelectionTask;
            window.DataContext = replacement;
            await pending; Settle(window);
            Assert.False(pane.SelectionVerified);
            Assert.True(string.IsNullOrEmpty(previewText.Text));
            fixture.ViewModel.PreviewFileCommand.Execute(fixture.File); Settle(window);
            Assert.False(window.FindControl<Border>("InlinePreviewHost")!.IsVisible);
            Assert.Empty(window.OwnedWindows);

            replacement.Results.Add(fixture.File);
            replacement.PreviewFileCommand.Execute(fixture.File);
            pending = pane.SelectionTask;
            window.Close(); await pending;
            Assert.False(pane.SelectionVerified);
            Assert.True(string.IsNullOrEmpty(previewText.Text));
            // A retained view model must no longer send a request into the disposed pane.
            replacement.PreviewFileCommand.Execute(fixture.File);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FileAndMatchFlyoutsRetainTheOriginalFindIdsAndUseTheirOwnRowData()
    {
        using var fixture = new PreviewFixture();
        var assistant = new RecordingFindAssistant();
        var vm = new MainWindowViewModel(findAssistant: assistant);
        vm.Results.Add(fixture.File);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            var list = window.FindControl<ListBox>("ResultsList")!;
            var row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
            var owner = Assert.Single(row.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "文件更多操作");
            var flyout = Assert.IsType<Flyout>(owner.Flyout);
            flyout.ShowAt(owner); Settle(window);
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            var action = FindButton(content, "FindOriginalFile");
            Assert.Same(fixture.File, action.DataContext);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("西瓜", Assert.Single(assistant.Requests).SearchText);

            var expander = Assert.Single(row.GetVisualDescendants().OfType<Expander>());
            expander.SetCurrentValue(Expander.IsExpandedProperty, true); Settle(window);
            var matches = Assert.Single(expander.GetVisualDescendants().OfType<ListBox>());
            var matchRow = Assert.IsAssignableFrom<Control>(matches.ContainerFromIndex(0));
            owner = Assert.Single(matchRow.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "匹配更多操作");
            flyout = Assert.IsType<Flyout>(owner.Flyout);
            flyout.ShowAt(owner); Settle(window);
            action = FindButton(Assert.IsAssignableFrom<Control>(flyout.Content), "FindOriginalMatch");
            Assert.Same(fixture.File.VisibleMatches[0], action.DataContext);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, assistant.Requests.Count);
            Assert.Equal(fixture.File.FullPath, assistant.Requests[1].FullPath);
            Assert.IsType<VirtualizingStackPanel>(matches.ItemsPanelRoot);
        }
        finally { window.Close(); }
    }

    private static Button FindButton(Control root, string id) =>
        Assert.Single(root.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == id);

    private static void AssertInside(MainWindow window, Control control)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(origin.X + control.Bounds.Width <= window.ClientSize.Width + 1);
        Assert.True(origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private sealed class RecordingFindAssistant : IOriginalFileFindAssistant
    {
        public List<FindAssistRequest> Requests { get; } = [];
        public Task<FindAssistResult> FindAsync(FindAssistRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new FindAssistResult(FindAssistStatus.ManualFindRequired, "fixture", request.SearchText));
        }
    }

    private sealed class PreviewFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "LocalDocSearch-design-" + Guid.NewGuid().ToString("N"));
        public MainWindowViewModel ViewModel { get; } = new();
        public FileResultViewModel File { get; }
        public PreviewFixture()
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "预览示例.txt");
            System.IO.File.WriteAllText(path, "甲西瓜乙");
            var match = new MatchItem("西瓜", "甲", "西瓜", "乙", new SourceLocation(1, 2), "line:1", 1);
            File = new FileResultViewModel(new FileResult(path, ".txt", System.IO.File.GetLastWriteTimeUtc(path), [match]));
            ViewModel.Results.Add(File);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
