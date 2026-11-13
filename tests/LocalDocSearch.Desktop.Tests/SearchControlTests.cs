using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class SearchControlTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [AvaloniaFact]
    public void IdleControlCommandsAreDisabledAndLeaveStatusUnchanged()
    {
        var vm = new MainWindowViewModel();
        var status = vm.StatusText;
        Assert.Equal(SearchControlState.Idle, vm.SearchState);
        Assert.Equal("暂停", vm.PauseResumeSearchText);
        AssertControls(vm, enabled: false);
        vm.PauseResumeSearchCommand.Execute(null);
        vm.StopSearchCommand.Execute(null);
        vm.CancelSearchCommand.Execute(null);
        Assert.Equal(status, vm.StatusText);
        Assert.Null(Control(vm));
    }

    [AvaloniaFact]
    public async Task ResumeKeepsTheSameQueryAndDoesNotClearOrReextractPublishedFiles()
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel();
        var resets = 0;
        var requested = false;
        vm.Results.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (change.Action != NotifyCollectionChangedAction.Add || requested) return;
            requested = true;
            vm.PauseResumeSearchCommand.Execute(null);
        };
        var paused = WaitForStateAsync(vm, SearchControlState.Paused);
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            await paused;
            Assert.True(vm.IsBusy);
            Assert.False(run.IsCompleted);
            Assert.StartsWith("已暂停", vm.StatusText);
            Assert.Equal("继续", vm.PauseResumeSearchText);
            AssertControls(vm, enabled: true);
            Assert.True(Control(vm)!.IsPaused);
            Assert.Equal(0, Pending<FileResult>(vm, "_pendingResults").Reader.Count);
            var first = vm.Results[0];
            var formula = vm.QueryFormula;
            // This file is already complete and visible. A restarted query would lose it.
            File.WriteAllText(first.FullPath, "changed after publication");
            var count = vm.Results.Count;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(count, vm.Results.Count);
            Assert.False(run.IsCompleted);
            vm.PauseResumeSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.Running, vm.SearchState);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(180, vm.LastSearchSummary.Progress.ScannedFiles);
            Assert.Equal(180, vm.Results.Count);
            Assert.Same(first, vm.Results[0]);
            Assert.Equal(formula, vm.QueryFormula);
            Assert.Equal(1, resets);
            Assert.Empty(vm.Issues);
            Assert.Equal(SearchControlState.Idle, vm.SearchState);
            AssertControls(vm, enabled: false);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaFact]
    public async Task StoppingWhilePausedExitsAndTheSecondRunUsesAFreshControl()
    {
        using var files = new FixtureScope();
        files.WriteMatches(120);
        var vm = files.ViewModel();
        var notifications = 0;
        vm.PauseResumeSearchCommand.CanExecuteChanged += (_, _) => notifications++;
        vm.StopSearchCommand.CanExecuteChanged += (_, _) => notifications++;
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        var oldControl = Control(vm);
        try
        {
            AssertControls(vm, enabled: true);
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.PauseRequested, vm.SearchState);
            Assert.StartsWith("正在暂停", vm.StatusText);
            await paused;
            var visibleAtStop = vm.Results.Count;
            vm.StopSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.Stopping, vm.SearchState);
            Assert.StartsWith("正在停止", vm.StatusText);
            AssertControls(vm, enabled: false);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
            Assert.Equal(visibleAtStop, vm.Results.Count);
            Assert.StartsWith("已停止", vm.StatusText);
            Assert.False(vm.IsBusy);
            Assert.Equal(SearchControlState.Idle, vm.SearchState);
            Assert.Null(Control(vm));
            Assert.False(oldControl!.IsPauseRequested);
            run = vm.StartSearchCommand.ExecuteAsync(null);
            Assert.NotSame(oldControl, Control(vm));
            Assert.Equal(SearchControlState.Running, vm.SearchState);
            Assert.Equal("暂停", vm.PauseResumeSearchText);
            AssertControls(vm, enabled: true);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(120, vm.Results.Count);
            Assert.True(notifications >= 8);
            Assert.Null(Control(vm));
            AssertControls(vm, enabled: false);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseAfterWorkerCompletionHoldsFinalizationUntilResumeOrStop(bool stop)
    {
        using var files = new FixtureScope();
        files.WriteMatches(2);
        var vm = files.ViewModel();
        vm.ApplySettings(vm.CaptureSettings() with { SortMode = 1 });
        vm.Results.CollectionChanged += (_, change) =>
        {
            // Final sorting runs only after the worker and pending result drain finish.
            // This deterministic checkpoint specifically exercises the completion gate.
            if (change.Action == NotifyCollectionChangedAction.Reset && vm.Results.Count == 2)
                vm.PauseResumeSearchCommand.Execute(null);
        };
        var paused = WaitForStateAsync(vm, SearchControlState.Paused);
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            await paused;
            Assert.Equal(2, vm.Results.Count);
            Assert.Null(vm.LastSearchSummary);
            Assert.False(run.IsCompleted);
            Assert.True(vm.IsBusy);
            Assert.True(Control(vm)!.IsPaused);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SearchControlState.Paused, vm.SearchState);
            Assert.False(run.IsCompleted);
            if (stop) vm.CancelSearchCommand.Execute(null);
            else vm.PauseResumeSearchCommand.Execute(null);
            await run.WaitAsync(Timeout);
            Assert.Equal(2, vm.Results.Count);
            Assert.Equal(stop ? SearchStatus.Cancelled : SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(SearchControlState.Idle, vm.SearchState);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaFact]
    public async Task FullResultQueueStillDrainsAfterPauseAndUnblocksTheProducer()
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel();
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            var channel = Pending<FileResult>(vm, "_pendingResults");
            // Hold the UI dispatcher only until the real worker fills the bounded queue.
            // No elapsed-time guess or artificial extractor determines this checkpoint.
            Assert.True(SpinWait.SpinUntil(() => channel.Reader.Count == 64, Timeout));
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.PauseRequested, vm.SearchState);
            await paused;
            Assert.Equal(0, channel.Reader.Count);
            Assert.True(vm.Results.Count >= 64);
            Assert.True(Control(vm)!.IsPaused);
            Assert.False(run.IsCompleted);
            vm.PauseResumeSearchCommand.Execute(null);
            await run.WaitAsync(Timeout);
            Assert.Equal(180, vm.Results.Count);
            Assert.Equal(180, vm.Results.Select(file => file.FullPath).Distinct().Count());
            using var diagnostic = JsonDocument.Parse(vm.CreateDiagnosticSummary());
            Assert.Equal(64, diagnostic.RootElement.GetProperty("PendingResultCapacity").GetInt32());
            Assert.Equal(64, diagnostic.RootElement.GetProperty("MaximumObservedPendingResults").GetInt32());
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaFact]
    public async Task FullIssueQueueStillDrainsBeforePausedAndStopPreservesAllReceivedIssues()
    {
        using var files = new FixtureScope();
        for (var i = 0; i < 350; i++) File.WriteAllBytes(Path.Combine(files.Root, $"bad-{i:000}.txt"), [0xff]);
        var vm = files.ViewModel();
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            var channel = Pending<SearchIssue>(vm, "_pendingIssues");
            Assert.True(SpinWait.SpinUntil(() => channel.Reader.Count == 256, Timeout));
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            await paused;
            Assert.Equal(0, channel.Reader.Count);
            Assert.True(vm.Issues.Count >= 256);
            var received = vm.Issues.Count;
            vm.StopSearchCommand.Execute(null);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
            Assert.True(vm.Issues.Count >= received);
            Assert.Equal(vm.LastSearchSummary.Diagnostics.ErrorCount, (long)vm.Issues.Count);
            using var diagnostic = JsonDocument.Parse(vm.CreateDiagnosticSummary());
            Assert.Equal(256, diagnostic.RootElement.GetProperty("PendingIssueCapacity").GetInt32());
            Assert.Equal(256, diagnostic.RootElement.GetProperty("MaximumObservedPendingIssues").GetInt32());
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOnFirstAddRetainsExactlyOneWholeFileForNewAndLegacyCommands(bool legacy)
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel();
        var atStop = -1;
        vm.Results.CollectionChanged += (_, change) =>
        {
            if (change.Action != NotifyCollectionChangedAction.Add || atStop >= 0) return;
            atStop = vm.Results.Count;
            if (legacy) vm.CancelSearchCommand.Execute(null);
            else vm.StopSearchCommand.Execute(null);
        };
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            await run.WaitAsync(Timeout);
            Assert.Equal(1, atStop);
            var file = Assert.Single(vm.Results);
            Assert.Equal(2, file.TotalOccurrences);
            Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
            Assert.StartsWith("已停止", vm.StatusText);
            Assert.Equal(SearchControlState.Idle, vm.SearchState);
            AssertControls(vm, enabled: false);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaFact]
    public async Task ButtonsReflectRunningPauseRequestedPausedAndStoppingAvailability()
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel();
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 640 };
        Task run = Task.CompletedTask;
        try
        {
            window.Show();
            Settle(window);
            var pause = Button(window, "PauseResumeSearch");
            var stop = Button(window, "StopSearch");
            Assert.False(pause.IsEffectivelyEnabled);
            Assert.False(stop.IsEffectivelyEnabled);
            Assert.Equal("暂停", pause.Content);
            Assert.Equal("停止", stop.Content);
            run = vm.StartSearchCommand.ExecuteAsync(null);
            Assert.True(pause.IsEffectivelyEnabled);
            Assert.True(stop.IsEffectivelyEnabled);
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            Assert.Equal("继续", pause.Content);
            Assert.Equal(SearchControlState.PauseRequested, vm.SearchState);
            await paused;
            Assert.True(pause.IsEffectivelyEnabled);
            Assert.True(stop.IsEffectivelyEnabled);
            vm.StopSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.Stopping, vm.SearchState);
            Assert.False(pause.IsEffectivelyEnabled);
            Assert.False(stop.IsEffectivelyEnabled);
            await run.WaitAsync(Timeout);
            Assert.Equal("暂停", pause.Content);
            Assert.False(pause.IsEffectivelyEnabled);
            Assert.False(stop.IsEffectivelyEnabled);
        }
        finally
        {
            await StopAndFinishAsync(vm, run);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(2d)]
    public void AllControlsStayOnTheInputRowAndResultsKeepSixtyPercentAtMinimumSize(double scale)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 64; i++) vm.Terms.Add(new string('词', 100) + i);
        var window = new MainWindow { DataContext = vm, Width = 850, Height = 640 };
        try
        {
            window.Show();
            window.SetRenderScaling(scale);
            Settle(window);
            var search = window.FindControl<Border>("SearchCard")!;
            var input = Assert.Single(search.GetVisualDescendants().OfType<Grid>(), grid => grid.Classes.Contains("search-input"));
            var start = Assert.Single(input.Children.OfType<Button>(), button => Equals(button.Content, "开始搜索"));
            var pause = Button(window, "PauseResumeSearch");
            var stop = Button(window, "StopSearch");
            Assert.Same(vm.StartSearchCommand, start.Command);
            Assert.Same(vm.PauseResumeSearchCommand, pause.Command);
            Assert.Same(vm.StopSearchCommand, stop.Command);
            Assert.Equal(5, input.ColumnDefinitions.Count);
            foreach (var button in new[] { start, pause, stop })
            {
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(Bounds(search, window).Contains(Bounds(button, window)));
                Assert.InRange(Math.Abs(Bounds(button, window).Center.Y - Bounds(start, window).Center.Y), 0, 1d);
            }
            Assert.True(Bounds(start, window).Right <= Bounds(pause, window).Left);
            Assert.True(Bounds(pause, window).Right <= Bounds(stop, window).Left);
            Assert.True(window.FindControl<TextBox>("KeywordBox")!.Bounds.Width >= 100);
            var list = window.FindControl<ListBox>("ResultsList")!;
            Assert.True(list.Bounds.Width * list.Bounds.Height / (window.ClientSize.Width * window.ClientSize.Height) >= .60);
            foreach (var id in new[] { "SearchConditionsCard", "SearchQueryFormula", "SearchResultsArea", "SearchProgressText", "SearchStatusText", "OpenQueryGrouping", "ResultsToolbar", "ResultFileCount" })
                Assert.Contains(window.GetVisualDescendants().OfType<Control>(), control => AutomationProperties.GetAutomationId(control) == id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PausedStatusUsesDrainedCountsEvenWithoutANewProgressReport()
    {
        using var files = new FixtureScope();
        using var cancellation = new CancellationTokenSource();
        var vm = files.ViewModel();
        var control = new SearchExecutionControl();
        var channel = Channel.CreateBounded<FileResult>(64);
        void SetField(string name, object value) => typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        void Invoke(string name, params object[] arguments) => typeof(MainWindowViewModel)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, arguments);
        SetField("_pendingResults", channel);
        SetField("_searchCancellation", cancellation);
        SetField("_searchExecutionControl", control);
        vm.IsBusy = true;
        vm.SearchState = SearchControlState.Running;
        vm.ProgressText = "已扫描 0 · 已发现文件 0 · 匹配 0 · 已展示 0 · 跳过/失败 0";
        for (var i = 0; i < 3; i++)
            Assert.True(channel.Writer.TryWrite(new FileResult(Path.Combine(files.Root, $"queued-{i}.txt"),
                ".txt", DateTime.UtcNow,
                [new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(1, 1))])));
        vm.PauseResumeSearchCommand.Execute(null);
        var parked = control.WaitIfPausedAsync(cancellation.Token).AsTask();
        try
        {
            // A real bounded channel can have results left after its latest progress
            // snapshot was consumed. Drain those results with no new progress report.
            for (var pass = 0; pass < 100 && channel.Reader.Count != 0; pass++) Invoke("DrainPending", 1);
            Assert.Equal(0, channel.Reader.Count);
            Assert.Equal(3, vm.Results.Count);
            Invoke("RefreshSearchControlState");
            Assert.Equal(SearchControlState.Paused, vm.SearchState);
            Assert.Contains("已展示文件 3", vm.ProgressText);
            Assert.Contains("已展示匹配 3", vm.ProgressText);
            var pausedProgress = vm.ProgressText;
            Invoke("DrainPending", 100);
            Invoke("RefreshSearchControlState");
            Assert.Equal(pausedProgress, vm.ProgressText);
        }
        finally { control.Resume(); await parked.WaitAsync(Timeout); }
    }

    private static void AssertControls(MainWindowViewModel vm, bool enabled)
    {
        Assert.Equal(enabled, vm.PauseResumeSearchCommand.CanExecute(null));
        Assert.Equal(enabled, vm.StopSearchCommand.CanExecute(null));
        Assert.Equal(enabled, vm.CancelSearchCommand.CanExecute(null));
    }

    private static async Task WaitForStateAsync(MainWindowViewModel vm, SearchControlState state)
    {
        if (vm.SearchState == state) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(vm.SearchState) && vm.SearchState == state) ready.TrySetResult();
        }
        vm.PropertyChanged += Changed;
        try
        {
            if (vm.SearchState == state) return;
            await ready.Task.WaitAsync(Timeout);
        }
        finally { vm.PropertyChanged -= Changed; }
    }

    private static async Task StopAndFinishAsync(MainWindowViewModel vm, Task run)
    {
        vm.CancelSearchCommand.Execute(null);
        await run.WaitAsync(Timeout);
    }

    private static SearchExecutionControl? Control(MainWindowViewModel vm) =>
        (SearchExecutionControl?)typeof(MainWindowViewModel).GetField("_searchExecutionControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);

    private static Channel<T> Pending<T>(MainWindowViewModel vm, string field) =>
        Assert.IsAssignableFrom<Channel<T>>(typeof(MainWindowViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));

    private static Button Button(Window window, string id) => Assert.Single(window.GetVisualDescendants().OfType<Button>(),
        button => AutomationProperties.GetAutomationId(button) == id);

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

    private sealed class FixtureScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-controls-" + Guid.NewGuid().ToString("N"));
        public FixtureScope() => Directory.CreateDirectory(Root);
        public void WriteMatches(int count)
        {
            for (var i = 0; i < count; i++) File.WriteAllText(Path.Combine(Root, $"match-{i:000}.txt"), "西瓜 西瓜");
        }
        public MainWindowViewModel ViewModel() => new() { RootFolder = Root, PendingTerm = "西瓜", ContextCharacters = 0 };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
