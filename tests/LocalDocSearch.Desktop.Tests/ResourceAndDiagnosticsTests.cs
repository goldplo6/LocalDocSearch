using System.Collections.Specialized;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class ResourceAndDiagnosticsTests
{
    [AvaloniaFact]
    public async Task GlobalBudgetKeepsWholeFilesAndExportsOnlyAggregateDiagnostics()
    {
        using var scope = new FixtureScope();
        scope.Write("FILE_PRIVATE_a.txt", "TERM_PRIVATE TERM_PRIVATE");
        scope.Write("FILE_PRIVATE_b.txt", "TERM_PRIVATE TERM_PRIVATE");
        var vm = scope.ViewModel("TERM_PRIVATE", new SearchResourceLimits { MaxTotalMatches = 3 });
        Assert.False(vm.HasDiagnostics);
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasDiagnostics);
        Assert.Equal(SearchStatus.Limited, vm.LastSearchSummary!.Status);
        Assert.Single(vm.Results);
        Assert.Equal(2, vm.Results[0].TotalOccurrences);
        Assert.Contains("资源上限", vm.StatusText);
        vm.Issues.Add(new SearchIssue(Path.Combine(scope.Root, "ERROR_PRIVATE.txt"), "MESSAGE_PRIVATE"));
        using var json = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        var root = json.RootElement;
        Assert.Equal("Limited", root.GetProperty("Status").GetString());
        Assert.Equal(1, root.GetProperty("DisplayedFiles").GetInt32());
        Assert.Equal(2, root.GetProperty("DisplayedOccurrences").GetInt32());
        Assert.Equal(1, root.GetProperty("ErrorCount").GetInt64());
        var decoded = string.Join("\n", Strings(root));
        foreach (var privateValue in new[] { "TERM_PRIVATE", "FILE_PRIVATE", "ERROR_PRIVATE", "MESSAGE_PRIVATE", "LocalDocSearch-private-", "MaxTotalMatches=3" })
            Assert.DoesNotContain(privateValue, decoded, StringComparison.Ordinal);
        Assert.Equal(64, root.GetProperty("PendingResultCapacity").GetInt32());
        Assert.Equal(256, root.GetProperty("PendingIssueCapacity").GetInt32());
    }

    [AvaloniaFact]
    public async Task CappedIssueDetailsKeepExactAggregateCounts()
    {
        using var scope = new FixtureScope();
        for (var i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(scope.Root, $"invalid-{i}.txt"), [0xff]);
        scope.Write("valid.txt", "西瓜");
        var vm = scope.ViewModel("西瓜", new SearchResourceLimits { MaxIssueDetails = 3 });
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(SearchStatus.Partial, vm.LastSearchSummary!.Status);
        Assert.Equal(12, vm.LastSearchSummary.Diagnostics.ErrorCount);
        Assert.Equal(9, vm.LastSearchSummary.Diagnostics.SuppressedIssueCount);
        Assert.Equal(4, vm.Issues.Count);
        Assert.Equal(3, vm.Issues.Count(issue => issue.Code == SearchIssueCode.Encoding));
        Assert.Single(vm.Issues, issue => issue.Code == SearchIssueCode.ResourceLimit);
        Assert.Single(vm.Results);
        using var json = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        Assert.Equal(9, json.RootElement.GetProperty("SuppressedIssueCount").GetInt64());
    }

    [AvaloniaFact]
    public async Task RealIssueFloodDrainsBoundedTransportWithoutDroppingDetails()
    {
        using var scope = new FixtureScope();
        for (var i = 0; i < 350; i++) File.WriteAllBytes(Path.Combine(scope.Root, $"invalid-{i}.txt"), [0xff]);
        var vm = scope.ViewModel("西瓜");
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(SearchStatus.Failed, vm.LastSearchSummary!.Status);
        Assert.Equal(350, vm.LastSearchSummary.Diagnostics.ErrorCount);
        Assert.Equal(350, vm.Issues.Count);
        Assert.All(vm.Issues, issue => Assert.Equal(SearchIssueCode.Encoding, issue.Code));
        using var json = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        Assert.InRange(json.RootElement.GetProperty("MaximumObservedPendingIssues").GetInt32(), 1, 256);
    }

    [AvaloniaFact]
    public async Task CancellationDuringResultBatchStopsFurtherDisplayAndUnblocksProducer()
    {
        using var scope = new FixtureScope();
        for (var i = 0; i < 180; i++) scope.Write($"result-{i:000}.txt", "西瓜");
        var vm = scope.ViewModel("西瓜");
        var acceptedAtCancel = -1;
        vm.Results.CollectionChanged += (_, _) =>
        {
            if (vm.Results.Count == 0 || acceptedAtCancel >= 0) return;
            acceptedAtCancel = vm.Results.Count;
            vm.CancelSearchCommand.Execute(null);
        };
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, acceptedAtCancel);
        Assert.Equal(acceptedAtCancel, vm.Results.Count);
        Assert.Equal(SearchStatus.Cancelled, vm.LastSearchSummary!.Status);
        using var json = JsonDocument.Parse(vm.CreateDiagnosticSummary());
        Assert.InRange(json.RootElement.GetProperty("MaximumObservedPendingResults").GetInt32(), 1, 64);
        Assert.True(json.RootElement.GetProperty("CancellationCompletionMilliseconds").GetDouble() >= 0);
    }

    [AvaloniaFact]
    public async Task FinalSortReordersWithOneResetWithoutReaddingEveryResult()
    {
        using var scope = new FixtureScope();
        for (var i = 1; i <= 12; i++) scope.Write($"file-{i:00}.txt", new string('a', i));
        var vm = scope.ViewModel("a");
        vm.ApplySettings(vm.CaptureSettings() with { SortMode = 2 });
        var added = 0;
        var resets = 0;
        vm.Results.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) added += e.NewItems!.Count;
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(12, added);
        Assert.Equal(2, resets); // Initial clear, then the single final reorder.
        Assert.Equal(Enumerable.Range(1, 12).Reverse(), vm.Results.Select(file => file.TotalOccurrences));
    }

    [AvaloniaFact]
    public void FileRowsExposeTheirTitlesToAccessibility()
    {
        var match = new MatchItem("西瓜", "", "西瓜", "", new SourceLocation(1, 1));
        var file = new FileResultViewModel(new FileResult("readable-file.txt", ".txt", DateTime.UtcNow, [match]));
        var vm = new MainWindowViewModel();
        vm.Results.Add(file);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var list = window.FindControl<ListBox>("ResultsList");
            Assert.NotNull(list);
            var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            Assert.Equal(file.Title, Avalonia.Automation.AutomationProperties.GetName(container));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TwoThousandLoadedMatchDetailsUseTheirOwnVirtualizedViewport()
    {
        var matches = Enumerable.Range(1, 2000)
            .Select(i => new MatchItem("西瓜", "上下文", "西瓜", "。", new SourceLocation(i, 1))).ToArray();
        var file = new FileResultViewModel(new FileResult("demo.txt", ".txt", DateTime.UtcNow, matches));
        while (file.HasMore) file.LoadMoreCommand.Execute(null);
        var vm = new MainWindowViewModel();
        vm.Results.Add(file);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var expander = window.GetVisualDescendants().OfType<Expander>().Single(e => ReferenceEquals(e.DataContext, file));
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var details = window.GetVisualDescendants().OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, file.VisibleMatches));
            Assert.Equal(2000, details.ItemCount);
            Assert.InRange(details.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 99);
            details.ScrollIntoView(1999);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.NotNull(details.ContainerFromIndex(1999));
            Assert.InRange(details.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 99);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DemoClearsRealSearchDiagnosticsAndUsesOneCollectionReset()
    {
        using var scope = new FixtureScope();
        scope.Write("sample.txt", "西瓜");
        var vm = scope.ViewModel("西瓜");
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasDiagnostics);
        var resets = 0;
        vm.Results.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        vm.LoadDemoCommand.Execute(null);
        Assert.Equal(10_000, vm.Results.Count);
        Assert.Equal(1, resets);
        Assert.False(vm.HasDiagnostics);
        Assert.Null(vm.LastSearchSummary);
        Assert.Contains("模拟", vm.DiagnosticsText);
    }

    [AvaloniaFact]
    public async Task InvalidDirectoryDoesNotLeaveAStaleMeasuringMessage()
    {
        using var scope = new FixtureScope();
        var vm = scope.ViewModel("西瓜");
        vm.RootFolder = Path.Combine(scope.Root, "does-not-exist");
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasDiagnostics);
        Assert.Null(vm.LastSearchSummary);
        Assert.Contains("未生成完整统计", vm.DiagnosticsText);
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var text in Strings(property.Value)) yield return text;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var text in Strings(item)) yield return text;
    }

    private sealed class FixtureScope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-private-" + Guid.NewGuid().ToString("N"));
        public FixtureScope() => Directory.CreateDirectory(Root);
        public void Write(string name, string text) => File.WriteAllText(Path.Combine(Root, name), text);
        public MainWindowViewModel ViewModel(string term, SearchResourceLimits? limits = null) =>
            new(resourceLimits: limits) { RootFolder = Root, PendingTerm = term, ContextCharacters = 0 };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
