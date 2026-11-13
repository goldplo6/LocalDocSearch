using System.Collections.Specialized;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public void RepeatedMetadataBindingsReuseStringsAndPreserveInitialMatchPage()
    {
        var matches = Enumerable.Range(1, 123)
            .Select(line => new MatchItem("西瓜", "前", "西瓜", "后", new SourceLocation(line, 2))).ToArray();
        var path = Path.Combine("folder", "report.DoCx");
        var modified = new DateTime(2026, 11, 13, 1, 2, 0, DateTimeKind.Utc);
        var file = new FileResultViewModel(new FileResult(path, ".docx", modified, matches));

        Assert.Equal("report.DoCx", file.FileName);
        Assert.Equal("DOCX", file.ExtensionLabel);
        Assert.Equal($"{123:N0} 处匹配", file.MatchCountText);
        Assert.Equal($"{modified.ToLocalTime():yyyy-MM-dd HH:mm}", file.ModifiedText);
        Assert.Equal($"{file.FileName}  ·  {123:N0} 处  ·  {file.ModifiedText}", file.Title);
        Assert.Same(file.FileName, file.FileName);
        Assert.Same(file.ExtensionLabel, file.ExtensionLabel);
        Assert.Same(file.MatchCountText, file.MatchCountText);
        Assert.Same(file.ModifiedText, file.ModifiedText);
        Assert.Same(file.Title, file.Title);
        Assert.True(file.IsWordFile);
        Assert.Equal(50, file.VisibleMatches.Count);
        Assert.Same(matches[0], file.VisibleMatches[0].SourceMatch);
        Assert.True(file.HasMore);
        file.LoadMoreCommand.Execute(null);
        Assert.Equal(100, file.VisibleMatches.Count);
        Assert.Same(matches[99], file.VisibleMatches[99].SourceMatch);
    }

    [AvaloniaFact]
    public async Task MixedPressurePreservesIndividualAddsAndBothBoundedChannels()
    {
        using var scope = new FixtureScope();
        for (var i = 0; i < 180; i++) scope.Write($"match-{i:000}.txt", "西瓜 西瓜");
        for (var i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(scope.Root, $"invalid-{i:000}.txt"), [0xff]);
        var vm = scope.ViewModel();
        var additions = 0;
        var resets = 0;
        vm.Results.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (change.Action != NotifyCollectionChangedAction.Add) return;
            Assert.Single(change.NewItems!);
            additions++;
        };

        await vm.StartSearchCommand.ExecuteAsync(null);

        Assert.Equal(SearchStatus.Partial, vm.LastSearchSummary!.Status);
        Assert.Equal(480, vm.LastSearchSummary.Progress.ScannedFiles);
        Assert.Equal(180, vm.Results.Count);
        Assert.Equal(180, vm.Results.Select(file => file.FullPath).Distinct().Count());
        Assert.Equal(180, additions);
        Assert.Equal(1, resets);
        Assert.All(vm.Results, file => Assert.Equal(2, file.TotalOccurrences));
        Assert.Equal(300, vm.Issues.Count);
        Assert.Equal(300, vm.LastSearchSummary.Diagnostics.ErrorCount);
        Assert.All(vm.Issues, issue => Assert.Equal(SearchIssueCode.Encoding, issue.Code));
        using var diagnostics = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        var json = diagnostics.RootElement;
        Assert.Equal(360, json.GetProperty("DisplayedOccurrences").GetInt32());
        Assert.Equal(64, json.GetProperty("PendingResultCapacity").GetInt32());
        Assert.Equal(256, json.GetProperty("PendingIssueCapacity").GetInt32());
        Assert.InRange(json.GetProperty("MaximumObservedPendingResults").GetInt32(), 0, 64);
        Assert.InRange(json.GetProperty("MaximumObservedPendingIssues").GetInt32(), 0, 256);
        var resultWaits = json.GetProperty("ResultBackpressureWaitCount").GetInt32();
        var issueWaits = json.GetProperty("IssueBackpressureWaitCount").GetInt32();
        Assert.InRange(resultWaits, 0, 180);
        Assert.InRange(issueWaits, 0, 300);
        Assert.InRange(json.GetProperty("BackpressureUiWakeups").GetInt32(), 0, resultWaits + issueWaits);
        Assert.True(json.GetProperty("ResultBackpressureWaitMilliseconds").GetDouble() >= 0);
        Assert.True(json.GetProperty("IssueBackpressureWaitMilliseconds").GetDouble() >= 0);
    }

    [AvaloniaFact]
    public async Task CancellingAtFirstAddThenRunningAgainLeavesNoPriorWakeupOrDiagnostics()
    {
        using var scope = new FixtureScope();
        for (var i = 0; i < 180; i++) scope.Write($"match-{i:000}.txt", "西瓜");
        var emptyRoot = Path.Combine(scope.Root, "next-run");
        Directory.CreateDirectory(emptyRoot);
        var vm = scope.ViewModel();
        var acceptedAtCancel = 0;
        vm.Results.CollectionChanged += (_, change) =>
        {
            if (change.Action != NotifyCollectionChangedAction.Add || acceptedAtCancel != 0) return;
            acceptedAtCancel = vm.Results.Count;
            vm.CancelSearchCommand.Execute(null);
        };

        await vm.StartSearchCommand.ExecuteAsync(null);

        Assert.Equal(1, acceptedAtCancel);
        Assert.Single(vm.Results);
        Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
        vm.RootFolder = emptyRoot;
        await vm.StartSearchCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Results);
        Assert.Empty(vm.Issues);
        Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
        using var diagnostics = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        var json = diagnostics.RootElement;
        Assert.Equal(0, json.GetProperty("ScannedFiles").GetInt32());
        Assert.Equal(0, json.GetProperty("ResultBackpressureWaitCount").GetInt32());
        Assert.Equal(0, json.GetProperty("IssueBackpressureWaitCount").GetInt32());
        Assert.Equal(0, json.GetProperty("BackpressureUiWakeups").GetInt32());
        Assert.Equal(0, json.GetProperty("ResultBackpressureWaitMilliseconds").GetDouble());
        Assert.Equal(0, json.GetProperty("IssueBackpressureWaitMilliseconds").GetDouble());
    }

    private sealed class FixtureScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-perf-" + Guid.NewGuid().ToString("N"));
        public FixtureScope() => Directory.CreateDirectory(Root);
        public void Write(string name, string text) => File.WriteAllText(Path.Combine(Root, name), text);
        public MainWindowViewModel ViewModel() => new() { RootFolder = Root, PendingTerm = "西瓜", ContextCharacters = 0 };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
