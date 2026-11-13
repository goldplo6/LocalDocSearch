using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class ConcurrentSearchControlTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(8)]
    public void InvalidWorkerCountsFailExplicitly(int workers) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MainWindowViewModel(maxConcurrency: workers));

    [Fact]
    public void StartupArgumentsRequireExplicitSettingsAndAnExactSupportedWorkerCount()
    {
        static (string? SettingsPath, int MaxConcurrency) Parse(params string[] args) =>
            ((string?, int))typeof(App).GetMethod("ParseStartupArguments", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { args })!;
        Assert.Equal(((string?)null, 1), Parse());
        Assert.Equal(("fixture.json", 1), Parse("--settings", "fixture.json"));
        foreach (var workers in new[] { 1, 2, 4 })
            Assert.Equal(("fixture.json", workers), Parse("--settings", "fixture.json", "--experiment-workers", workers.ToString()));
        foreach (var args in new[]
        {
            new[] { "--experiment-workers", "2" },
            new[] { "--settings", "fixture.json", "--experiment-workers" },
            new[] { "--settings", "fixture.json", "--experiment-workers", "3" },
            new[] { "--settings", "fixture.json", "--experiment-workers", "0" },
            new[] { "--settings", "fixture.json", "--experiment-workers", "two" },
            new[] { "--settings", "fixture.json", "--experiment-workers", "02" },
            new[] { "--settings", "", "--experiment-workers", "2" },
            new[] { "--settings", "fixture.json", "--experiment-workers", "4", "extra" }
        })
        {
            var error = Assert.Throws<TargetInvocationException>(() => Parse(args));
            Assert.IsType<ArgumentException>(error.InnerException);
            Assert.Contains("启动参数无效", error.InnerException!.Message);
        }
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task FullResultQueuePauseFreezesResultsIssuesAndProgressThenResumeCompletes(int workers)
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel(workers);
        var resets = 0;
        vm.Results.CollectionChanged += (_, change) => { if (change.Action == NotifyCollectionChangedAction.Reset) resets++; };
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            var queue = Pending<FileResult>(vm, "_pendingResults");
            Assert.True(SpinWait.SpinUntil(() => queue.Reader.Count == 64, Timeout));
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            await paused;
            Assert.True(vm.Results.Count >= 64);
            await AssertFrozenAsync(vm, run);
            var first = vm.Results[0];
            var formula = vm.QueryFormula;
            File.WriteAllText(first.FullPath, "changed after publication");
            vm.PauseResumeSearchCommand.Execute(null);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(180, vm.LastSearchSummary.Progress.ScannedFiles);
            Assert.Equal(180, vm.Results.Count);
            Assert.Equal(180, vm.Results.Select(file => file.FullPath).Distinct().Count());
            Assert.Equal(360, vm.Results.Sum(file => file.TotalOccurrences));
            Assert.Contains(first, vm.Results);
            Assert.Equal(formula, vm.QueryFormula);
            Assert.Equal(1, resets);
            Assert.Empty(vm.Issues);
            AssertIdle(vm);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task FullIssueQueuePauseFreezesAllPublishedStateThenResumePreservesEveryIssue(int workers)
    {
        using var files = new FixtureScope();
        files.WriteInvalid(350);
        var vm = files.ViewModel(workers);
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        try
        {
            var queue = Pending<SearchIssue>(vm, "_pendingIssues");
            Assert.True(SpinWait.SpinUntil(() => queue.Reader.Count == 256, Timeout));
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            await paused;
            Assert.True(vm.Issues.Count >= 256);
            await AssertFrozenAsync(vm, run);
            vm.PauseResumeSearchCommand.Execute(null);
            await run.WaitAsync(Timeout);
            Assert.Equal(350, vm.LastSearchSummary!.Progress.ScannedFiles);
            Assert.Equal(350, vm.LastSearchSummary.Progress.SkippedFiles);
            Assert.Equal(350, vm.LastSearchSummary.Diagnostics.ErrorCount);
            Assert.Equal(350, vm.Issues.Count);
            Assert.Equal(350, vm.Issues.Select(issue => issue.Path).Distinct().Count());
            Assert.Empty(vm.Results);
            AssertIdle(vm);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task StopWhilePausedAddsNoResultsAndRestartUsesAFreshControl(int workers)
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel(workers);
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        var oldControl = Control(vm);
        try
        {
            var queue = Pending<FileResult>(vm, "_pendingResults");
            Assert.True(SpinWait.SpinUntil(() => queue.Reader.Count == 64, Timeout));
            var paused = WaitForStateAsync(vm, SearchControlState.Paused);
            vm.PauseResumeSearchCommand.Execute(null);
            await paused;
            await AssertFrozenAsync(vm, run);
            var published = vm.Results.ToArray();
            var issues = vm.Issues.ToArray();
            vm.StopSearchCommand.Execute(null);
            Assert.Equal(SearchControlState.Stopping, vm.SearchState);
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
            Assert.Equal(published, vm.Results.ToArray());
            Assert.Equal(issues, vm.Issues.ToArray());
            AssertIdle(vm);
            var stoppedProgress = vm.ProgressText;
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(published, vm.Results.ToArray());
            Assert.Equal(stoppedProgress, vm.ProgressText);
            run = vm.StartSearchCommand.ExecuteAsync(null);
            Assert.NotSame(oldControl, Control(vm));
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(180, vm.Results.Count);
            Assert.Equal(180, vm.Results.Select(file => file.FullPath).Distinct().Count());
            AssertIdle(vm);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task StopWhileRunningRetainsExactlyThePublishedWholeFileAndRestartCompletes(int workers)
    {
        using var files = new FixtureScope();
        files.WriteMatches(180);
        var vm = files.ViewModel(workers);
        var stopped = false;
        vm.Results.CollectionChanged += (_, change) =>
        {
            if (stopped || change.Action != NotifyCollectionChangedAction.Add) return;
            stopped = true;
            Assert.Equal(SearchControlState.Running, vm.SearchState);
            vm.StopSearchCommand.Execute(null);
        };
        var run = vm.StartSearchCommand.ExecuteAsync(null);
        var oldControl = Control(vm);
        try
        {
            await run.WaitAsync(Timeout);
            Assert.True(stopped);
            Assert.Equal(2, Assert.Single(vm.Results).TotalOccurrences);
            Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
            AssertIdle(vm);
            run = vm.StartSearchCommand.ExecuteAsync(null);
            Assert.NotSame(oldControl, Control(vm));
            await run.WaitAsync(Timeout);
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
            Assert.Equal(180, vm.Results.Count);
            Assert.Equal(360, vm.Results.Sum(file => file.TotalOccurrences));
            Assert.Empty(vm.Issues);
            AssertIdle(vm);
        }
        finally { await StopAndFinishAsync(vm, run); }
    }

    private static async Task AssertFrozenAsync(MainWindowViewModel vm, Task run)
    {
        Assert.True(Control(vm)!.IsPaused);
        Assert.Equal(0, Pending<FileResult>(vm, "_pendingResults").Reader.Count);
        Assert.Equal(0, Pending<SearchIssue>(vm, "_pendingIssues").Reader.Count);
        var results = vm.Results.ToArray();
        var issues = vm.Issues.ToArray();
        var progress = vm.ProgressText;
        for (var sample = 0; sample < 6; sample++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SearchControlState.Paused, vm.SearchState);
            Assert.True(vm.IsBusy);
            Assert.False(run.IsCompleted);
            Assert.True(Control(vm)!.IsPaused);
            Assert.Equal(results, vm.Results.ToArray());
            Assert.Equal(issues, vm.Issues.ToArray());
            Assert.Equal(progress, vm.ProgressText);
        }
    }

    private static void AssertIdle(MainWindowViewModel vm)
    {
        Assert.False(vm.IsBusy);
        Assert.Equal(SearchControlState.Idle, vm.SearchState);
        Assert.Null(Control(vm));
        Assert.False(vm.PauseResumeSearchCommand.CanExecute(null));
        Assert.False(vm.StopSearchCommand.CanExecute(null));
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
        try { if (vm.SearchState != state) await ready.Task.WaitAsync(Timeout); }
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

    private sealed class FixtureScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-concurrent-controls-" + Guid.NewGuid().ToString("N"));
        public FixtureScope() => Directory.CreateDirectory(Root);
        public void WriteMatches(int count)
        {
            for (var i = 0; i < count; i++) File.WriteAllText(Path.Combine(Root, $"match-{i:000}.txt"), "西瓜 西瓜");
        }
        public void WriteInvalid(int count)
        {
            for (var i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(Root, $"bad-{i:000}.txt"), [0xff]);
        }
        public MainWindowViewModel ViewModel(int workers) => new(maxConcurrency: workers) { RootFolder = Root, PendingTerm = "西瓜", ContextCharacters = 0 };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
