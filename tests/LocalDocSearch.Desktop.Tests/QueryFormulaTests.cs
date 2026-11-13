using Avalonia.Headless.XUnit;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class QueryFormulaTests
{
    [AvaloniaFact]
    public void PendingFormulaAndInputHintFollowTheNextConnector()
    {
        var vm = new MainWindowViewModel();
        vm.PendingTerm = "西瓜";
        Assert.Equal("公式：西瓜", vm.QueryFormula);
        vm.AddTermCommand.Execute(null);
        vm.PendingTerm = "苹果";
        Assert.Equal("公式：西瓜 AND 苹果", vm.QueryFormula);
        Assert.Contains("AND", vm.PendingTermHint);
        vm.AddTermCommand.Execute(null);
        vm.UseOr = true; vm.PendingTerm = "荔枝";
        Assert.Equal("公式：(西瓜 AND 苹果) OR 荔枝", vm.QueryFormula);
        Assert.Contains("OR", vm.PendingTermHint);
        vm.AddTermCommand.Execute(null);
        Assert.Equal("公式：(西瓜 AND 苹果) OR 荔枝", vm.QueryFormula);
        Assert.Equal(new[] { "", "AND", "OR" }, vm.QueryChips.Select(chip => chip.Connector));
    }

    [AvaloniaFact]
    public void EditingAndRemovingChipsUpdatesTheActualFormulaWithoutStaleLinks()
    {
        var vm = new MainWindowViewModel();
        foreach (var term in new[] { "西瓜", "苹果", "荔枝" }) { vm.PendingTerm = term; vm.AddTermCommand.Execute(null); }
        vm.ToggleTermConnectorCommand.Execute("荔枝");
        Assert.Equal("公式：(西瓜 AND 苹果) OR 荔枝", vm.QueryFormula);
        vm.RemoveTermCommand.Execute("苹果");
        Assert.Equal("公式：西瓜 OR 荔枝", vm.QueryFormula);
        vm.IsBusy = true; vm.ToggleTermConnectorCommand.Execute("荔枝");
        Assert.Equal("公式：西瓜 OR 荔枝", vm.QueryFormula);
        vm.IsBusy = false; vm.Terms.Clear();
        vm.UseAnd = true; vm.Terms.Add("新词"); vm.Terms.Add("荔枝");
        Assert.Equal("公式：新词 AND 荔枝", vm.QueryFormula);
    }

    [AvaloniaFact]
    public void LiteralOperatorInOneKeywordRemainsOneTermAndIsQuoted()
    {
        var vm = new MainWindowViewModel { PendingTerm = "西瓜AND苹果" };
        vm.AddTermCommand.Execute(null);
        Assert.Single(vm.Terms);
        Assert.Equal("公式：\"西瓜AND苹果\"", vm.QueryFormula);
        vm.PendingTerm = "西瓜AND苹果"; vm.AddTermCommand.Execute(null);
        Assert.Single(vm.Terms); Assert.Single(vm.QueryChips);
    }

    [AvaloniaFact]
    public async Task OverLimitPendingTermBlocksSearchAndDoesNotEnterDisplayedFormula()
    {
        var vm = new MainWindowViewModel(resourceLimits: new SearchResourceLimits { MaxTerms = 2 });
        foreach (var term in new[] { "西瓜", "苹果" })
        {
            vm.PendingTerm = term;
            vm.AddTermCommand.Execute(null);
        }
        vm.UseOr = true;
        vm.PendingTerm = "荔枝";
        Assert.Equal("公式：西瓜 AND 苹果", vm.QueryFormula);
        Assert.Contains("上限", vm.PendingTermHint);
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal("荔枝", vm.PendingTerm);
        Assert.Equal("关键词数量须不超过 2。", vm.StatusText);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.LastSearchSummary);
        Assert.Equal(2, vm.Terms.Count);
    }

    [AvaloniaFact]
    public async Task MixedFormulaDisplayedByTheWindowQualifiesRealFilesWithAndPrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalDocSearch-formula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "both.txt"), "西瓜\n苹果");
            File.WriteAllText(Path.Combine(root, "or.txt"), "荔枝");
            File.WriteAllText(Path.Combine(root, "no.txt"), "西瓜");
            File.WriteAllText(Path.Combine(root, "also.txt"), "西瓜 荔枝");
            var vm = new MainWindowViewModel { RootFolder = root };
            foreach (var term in new[] { "西瓜", "苹果" }) { vm.PendingTerm = term; vm.AddTermCommand.Execute(null); }
            vm.UseOr = true; vm.PendingTerm = "荔枝";
            Assert.Equal("公式：(西瓜 AND 苹果) OR 荔枝", vm.QueryFormula);
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "also.txt", "both.txt", "or.txt" }, vm.Results.Select(f => Path.GetFileName(f.FullPath)).OrderBy(n => n));
            Assert.Equal(5, vm.Results.Sum(f => f.TotalOccurrences));
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
        }
        finally { Directory.Delete(root, true); }
    }
}
