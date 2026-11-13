using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class QueryGroupingControlTests
{
    [AvaloniaFact]
    public void MainWindowOpensTheActualEditorAppliesGroupsAndCancelsFurtherDraftChanges()
    {
        var vm = FlatQuery();
        var main = new MainWindow { DataContext = vm };
        try
        {
            main.Show(); Settle(main);
            Click(main, main.FindControl<Button>("GroupingButton")!);
            var editor = Assert.Single(main.OwnedWindows.OfType<QueryGroupingWindow>());
            Settle(editor);
            SelectLiteral(editor, "苹果"); SelectLiteral(editor, "荔枝");
            Click(editor, editor.FindControl<Button>("GroupOrButton")!);
            var state = Assert.IsType<QueryEditorViewModel>(editor.DataContext);
            Assert.Equal("西瓜 AND (苹果 OR 荔枝)", editor.FindControl<TextBox>("FormulaPreview")!.Text);
            Assert.Equal(state.Model.Snapshot.Formula, state.Formula);
            Click(editor, editor.FindControl<Button>("ApplyButton")!);
            Settle(main);
            Assert.Equal("公式：西瓜 AND (苹果 OR 荔枝)", vm.QueryFormula);
            Assert.NotNull(vm.QueryExpression); Assert.True(vm.HasGroupedQuery);
            Assert.False(vm.IsChipEditingEnabled);
            Assert.All(vm.QueryChips, chip => { Assert.True(chip.IsStructured); Assert.False(chip.HasConnector); });
            var keywordButtons = main.GetVisualDescendants().OfType<Button>()
                .Where(button => AutomationProperties.GetAutomationId(button) == "SearchKeywordChip").ToArray();
            Assert.Equal(3, keywordButtons.Length); Assert.All(keywordButtons, button => Assert.False(button.IsEnabled));

            Click(main, main.FindControl<Button>("GroupingButton")!);
            editor = Assert.Single(main.OwnedWindows.OfType<QueryGroupingWindow>()); Settle(editor);
            var secondState = Assert.IsType<QueryEditorViewModel>(editor.DataContext);
            SelectRow(editor, Assert.Single(secondState.Rows, row => row.IsGroup));
            Click(editor, editor.FindControl<Button>("UngroupButton")!);
            Assert.Equal("(西瓜 AND 苹果) OR 荔枝", secondState.Formula);
            Click(editor, editor.FindControl<Button>("CancelButton")!); Settle(main);
            Assert.Equal("公式：西瓜 AND (苹果 OR 荔枝)", vm.QueryFormula);
        }
        finally { foreach (var owned in main.OwnedWindows.ToArray()) owned.Close(); main.Close(); }
    }

    [AvaloniaFact]
    public void ActualRowsSupportNestedGroupsConnectorEditingUngroupAndChildDeletion()
    {
        var state = FlatQuery().CreateQueryEditor()!;
        var window = new QueryGroupingWindow(state);
        try
        {
            window.Show(); Settle(window);
            SelectLiteral(window, "苹果"); SelectLiteral(window, "荔枝");
            Click(window, window.FindControl<Button>("GroupOrButton")!);
            SelectRow(window, Assert.Single(state.Rows, row => row.IsGroup));
            Click(window, window.FindControl<Button>("GroupAndButton")!);
            Assert.Equal("西瓜 AND ((苹果 OR 荔枝))", state.Formula);
            Assert.Equal(2, state.Rows.Count(row => row.IsGroup));
            var outer = state.Rows.First(row => row.IsGroup);
            SelectRow(window, outer);
            Click(window, window.FindControl<Button>("UngroupButton")!);
            Assert.Equal("西瓜 AND (苹果 OR 荔枝)", state.Formula);
            var child = state.Rows.Single(row => row.Text == "荔枝" && !row.IsGroup);
            var connector = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == child.ConnectorId);
            connector.SelectedIndex = 0; Settle(window);
            Assert.Equal("西瓜 AND (苹果 AND 荔枝)", state.Formula);
            Assert.Same(child, state.Rows.Single(row => row.Id == child.Id));
            Assert.Equal(0, connector.SelectedIndex);
            SelectLiteral(window, "苹果");
            Click(window, window.FindControl<Button>("DeleteButton")!);
            Assert.Equal("西瓜 AND (荔枝)", state.Formula);
            SelectRow(window, Assert.Single(state.Rows, row => row.IsGroup));
            window.FindControl<TextBox>("LiteralInput")!.Text = "梨";
            window.FindControl<ComboBox>("NextConnector")!.SelectedIndex = 1; Settle(window);
            Click(window, window.FindControl<Button>("AddGroupButton")!);
            Assert.Equal("西瓜 AND (荔枝 OR 梨)", state.Formula);
            Assert.True(state.TryCapture(out var snapshot));
            Assert.Equal(state.Formula, SearchFormula.Format(snapshot!.Expression!, snapshot.Terms));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ActualInputPreservesDuplicatesAndLiteralOperatorsAndReportsResourceLimits()
    {
        var state = new QueryEditorViewModel(new QueryEditorModel(["AND", "()"], limits: new SearchResourceLimits { MaxTerms = 3 }));
        var window = new QueryGroupingWindow(state);
        try
        {
            window.Show(); Settle(window);
            var input = window.FindControl<TextBox>("LiteralInput")!;
            input.Text = "AND"; Settle(window);
            Click(window, window.FindControl<Button>("AddRootButton")!);
            Assert.Equal(new[] { "AND", "()", "AND" }, state.Model.Snapshot.Terms);
            Assert.Equal(3, state.Rows.Select(row => row.Id).Distinct().Count());
            Assert.Contains("\"()\"", state.Formula); Assert.Contains("\"AND\"", state.Formula);
            var before = state.Model.Snapshot;
            input.Text = "fourth"; Settle(window);
            Click(window, window.FindControl<Button>("AddRootButton")!);
            Assert.Same(before, state.Model.Snapshot); Assert.Equal("fourth", input.Text);
            Assert.Contains("3", state.Message);
            Assert.False(state.TryCapture(out _)); Assert.Contains("待添加", state.Message);
            Assert.NotNull(FindById<Control>(window, "QueryGroupMessage"));
            Assert.All(state.Rows, row => Assert.NotNull(FindById<CheckBox>(window, row.SelectionId)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InvalidSelectionAndBusyCommandsPreserveTheDraftAndMainQuery()
    {
        var vm = FlatQuery();
        var state = vm.CreateQueryEditor()!;
        state.Rows[0].IsSelected = true; state.Rows[2].IsSelected = true;
        var before = state.Model.Snapshot;
        state.GroupOrCommand.Execute(null);
        Assert.Same(before, state.Model.Snapshot); Assert.Contains("相邻", state.Message);
        state.ClearSelectionCommand.Execute(null);
        state.Rows[1].IsSelected = true; state.Rows[2].IsSelected = true;
        vm.IsBusy = true;
        state.GroupOrCommand.Execute(null); state.DeleteSelectedCommand.Execute(null);
        state.PendingLiteral = "D"; state.AddToRootCommand.Execute(null);
        Assert.Same(before, state.Model.Snapshot); Assert.Equal("D", state.PendingLiteral);
        Assert.Contains("搜索进行中", state.Message); Assert.False(state.TryCapture(out _));
        Assert.Null(vm.CreateQueryEditor()); Assert.False(vm.ApplyGroupedQuery(before));
        vm.PendingTerm = "E"; vm.AddTermCommand.Execute(null); vm.Terms.Add("F");
        Assert.Equal(new[] { "西瓜", "苹果", "荔枝" }, vm.Terms);
        vm.IsBusy = false;
        state.PendingLiteral = string.Empty;
        state.GroupOrCommand.Execute(null);
        Assert.True(state.TryCapture(out var snapshot));
        Assert.Equal("E", vm.PendingTerm);
        Assert.False(vm.ApplyGroupedQuery(snapshot!));
        Assert.Equal("E", vm.PendingTerm);
        Assert.Equal(new[] { "西瓜", "苹果", "荔枝" }, vm.Terms);
        Assert.Contains("待添加", vm.StatusText);
        vm.PendingTerm = string.Empty; // The caller explicitly dismisses its independently typed main input.
        Assert.True(vm.ApplyGroupedQuery(snapshot!));
        Assert.Equal("公式：西瓜 AND (苹果 OR 荔枝)", vm.QueryFormula);
    }

    [AvaloniaFact]
    public void MainGroupedTermsStayGuardedAndPendingAddIsAtTheRoot()
    {
        var vm = FlatQuery(); var editor = vm.CreateQueryEditor()!;
        editor.Rows[1].IsSelected = true; editor.Rows[2].IsSelected = true; editor.GroupOrCommand.Execute(null);
        Assert.True(editor.TryCapture(out var snapshot)); Assert.True(vm.ApplyGroupedQuery(snapshot!));
        vm.Terms.CollectionChanged += (_, _) => { }; // More than one observer must not trigger restoration reentrancy.
        vm.Terms.Add("unrelated"); vm.Terms.Remove("苹果"); vm.Terms.Clear();
        vm.RemoveTermCommand.Execute("西瓜"); vm.ToggleTermConnectorCommand.Execute("苹果");
        Assert.Equal("公式：西瓜 AND (苹果 OR 荔枝)", vm.QueryFormula);
        Assert.Equal(new[] { "西瓜", "苹果", "荔枝" }, vm.Terms);
        vm.UseOr = true; vm.PendingTerm = "西瓜";
        Assert.Equal("公式：(西瓜 AND (苹果 OR 荔枝)) OR 西瓜", vm.QueryFormula);
        vm.AddTermCommand.Execute(null);
        Assert.Equal(4, vm.Terms.Count); Assert.Equal(2, vm.Terms.Count(term => term == "西瓜"));
        Assert.Equal("公式：(西瓜 AND (苹果 OR 荔枝)) OR 西瓜", vm.QueryFormula);
        var reopened = vm.CreateQueryEditor()!;
        Assert.Equal(vm.QueryFormula, "公式：" + reopened.Formula);
    }

    [AvaloniaFact]
    public void PendingTermIsCommittedBeforeOpeningAndAnOverLimitTermKeepsTheDialogClosed()
    {
        var vm = new MainWindowViewModel(resourceLimits: new SearchResourceLimits { MaxTerms = 2 });
        vm.PendingTerm = "A"; vm.AddTermCommand.Execute(null); vm.PendingTerm = "B";
        var editor = vm.CreateQueryEditor(); Assert.NotNull(editor);
        Assert.Equal(new[] { "A", "B" }, vm.Terms); Assert.Equal(string.Empty, vm.PendingTerm);
        editor!.PendingLiteral = "cancelled"; editor.AddToRootCommand.Execute(null);
        Assert.Equal(new[] { "A", "B" }, vm.Terms);
        vm.PendingTerm = "C";
        Assert.Null(vm.CreateQueryEditor()); Assert.Equal("C", vm.PendingTerm);
        Assert.Equal(new[] { "A", "B" }, vm.Terms); Assert.Contains("2", vm.StatusText);
    }

    [AvaloniaFact]
    public async Task GroupedRequestProducesThreeFilesSevenHitsWhereFlatQueryProducesFiveFilesTenHits()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalDocSearch-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var terms = new[] { "西瓜", "苹果", "荔枝" };
            for (var bits = 0; bits < 8; bits++)
                File.WriteAllText(Path.Combine(root, $"truth-{bits}.txt"),
                    string.Join(" ", terms.Where((_, index) => (bits & (1 << index)) != 0)));
            var vm = FlatQuery(); vm.RootFolder = root;
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.Equal(5, vm.Results.Count); Assert.Equal(10, vm.Results.Sum(result => result.TotalOccurrences));
            var editor = vm.CreateQueryEditor()!;
            editor.Rows[1].IsSelected = true; editor.Rows[2].IsSelected = true; editor.GroupOrCommand.Execute(null);
            Assert.True(editor.TryCapture(out var snapshot)); Assert.True(vm.ApplyGroupedQuery(snapshot!));
            Assert.Equal("公式：西瓜 AND (苹果 OR 荔枝)", vm.QueryFormula);
            await vm.StartSearchCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "truth-3.txt", "truth-5.txt", "truth-7.txt" },
                vm.Results.Select(result => Path.GetFileName(result.FullPath)).OrderBy(name => name));
            Assert.Equal(7, vm.Results.Sum(result => result.TotalOccurrences));
            Assert.Equal(SearchStatus.Completed, vm.LastSearchSummary!.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task DeletingEveryLiteralAppliesAnEmptyQueryAndPreventsSearching()
    {
        var vm = FlatQuery(); var editor = vm.CreateQueryEditor()!;
        foreach (var row in editor.Rows) row.IsSelected = true;
        editor.DeleteSelectedCommand.Execute(null); Assert.True(editor.TryCapture(out var snapshot));
        Assert.True(vm.ApplyGroupedQuery(snapshot!)); Assert.Empty(vm.Terms); Assert.Null(vm.QueryExpression);
        await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy); Assert.Null(vm.LastSearchSummary);
        Assert.Equal("公式：请添加关键词", vm.QueryFormula);
    }

    private static MainWindowViewModel FlatQuery()
    {
        var vm = new MainWindowViewModel();
        foreach (var literal in new[] { "西瓜", "苹果" }) { vm.PendingTerm = literal; vm.AddTermCommand.Execute(null); }
        vm.UseOr = true; vm.PendingTerm = "荔枝"; vm.AddTermCommand.Execute(null);
        return vm;
    }

    private static void SelectLiteral(QueryGroupingWindow window, string text)
    {
        var state = Assert.IsType<QueryEditorViewModel>(window.DataContext);
        SelectRow(window, Assert.Single(state.Rows, row => !row.IsGroup && row.Text == text));
    }

    private static void SelectRow(QueryGroupingWindow window, QueryEditorRowViewModel row) =>
        Click(window, FindById<CheckBox>(window, row.SelectionId));

    private static T FindById<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseMove(point.Value); window.MouseDown(point.Value, MouseButton.Left); window.MouseUp(point.Value, MouseButton.Left);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}
