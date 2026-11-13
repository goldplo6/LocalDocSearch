using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using LocalDocSearch.Navigation;
using LocalDocSearch.Settings;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class AdvancedSearchTests
{
    [AvaloniaFact]
    public void ActualEntryHasTwoTopLevelPagesAndManualInputNeverAppliesAnInvalidFallback()
    {
        var vm = new MainWindowViewModel { PendingTerm = "original" };
        vm.AddTermCommand.Execute(null);
        var originalFormula = vm.QueryFormula;
        var main = new MainWindow { DataContext = vm };
        try
        {
            main.Show(); Settle(main);
            var entry = main.FindControl<Button>("GroupingButton")!;
            Assert.Equal("高级搜索…", entry.Content);
            Click(main, entry);
            var dialog = Assert.Single(main.OwnedWindows.OfType<QueryGroupingWindow>());
            Settle(dialog);
            Assert.Equal("高级搜索", dialog.Title);
            var tabs = dialog.FindControl<TabControl>("SearchTabs")!;
            Assert.Equal(new[] { "逻辑搜索", "正则匹配" }, tabs.Items.OfType<TabItem>().Select(tab => (string)tab.Header!));
            var state = Assert.IsType<QueryEditorViewModel>(dialog.DataContext);
            dialog.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(dialog);
            var input = dialog.FindControl<TextBox>("ManualFormulaInput")!;
            Assert.True(input.AcceptsReturn); Assert.False(input.AcceptsTab);
            input.Text = "A AND (B OR C)"; Settle(dialog);
            Assert.True(state.CanApply);
            Assert.Equal("A AND (B OR C)", dialog.FindControl<TextBox>("FormulaPreview")!.Text);
            input.Text = "A AND (B OR )"; Settle(dialog);
            Assert.False(dialog.FindControl<Button>("ApplyButton")!.IsEnabled);
            Assert.False(state.TryCapture(out var rejected)); Assert.Null(rejected);
            Assert.True(state.ManualErrorOffset >= 0); Assert.Contains("位置", state.ManualError);
            Assert.DoesNotContain("A AND (B OR C)", dialog.FindControl<TextBox>("FormulaPreview")!.Text!);
            var error = ById<TextBlock>(dialog, "QueryManualError");
            Assert.Equal(state.ManualError, error.Text);
            Assert.Equal(state.ManualError, AutomationProperties.GetName(error));
            state.UseVisualEditorCommand.Execute(null); Settle(dialog);
            Assert.True(state.IsManualEditor); Assert.Equal("A AND (B OR )", input.Text);
            Click(dialog, dialog.FindControl<Button>("CancelButton")!);
            Assert.Equal(originalFormula, vm.QueryFormula);
        }
        finally { foreach (var owned in main.OwnedWindows.ToArray()) owned.Close(); main.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("A AND (B OR C)")]
    [InlineData("(A OR B) AND C")]
    [InlineData("A AND B OR C AND D")]
    [InlineData("((\"AND\" OR \"()\")) AND \"a b\"")]
    [InlineData("\"duplicate\" OR \"duplicate\"")]
    public void ManualVisualManualRoundTripKeepsTheActualCanonicalFormula(string formula)
    {
        var state = new QueryEditorViewModel(new QueryEditorModel([]));
        var dialog = new QueryGroupingWindow(state);
        try
        {
            dialog.Show(); Settle(dialog);
            dialog.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(dialog);
            dialog.FindControl<TextBox>("ManualFormulaInput")!.Text = formula; Settle(dialog);
            var parsed = LogicalQueryParser.Parse(formula); Assert.True(parsed.Success, parsed.Error);
            Click(dialog, ById<Button>(dialog, "QueryUseVisualEditor"));
            Assert.True(state.IsVisualEditor);
            Assert.Equal(parsed.Formula, state.Model.Snapshot.Formula);
            Assert.Equal(parsed.Terms, state.Model.Snapshot.Terms);
            Assert.Equal(parsed.Terms.Count, state.Rows.Where(row => !row.IsGroup).Select(row => row.Id).Distinct().Count());
            Assert.True(state.TryCapture(out var snapshot));
            Assert.Equal(parsed.Formula, SearchFormula.Format(snapshot!.Expression!, snapshot.Terms));
            dialog.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(dialog);
            Assert.Equal(parsed.Formula, state.ManualFormula);
            Assert.True(state.TryCapture(out var second)); Assert.Equal(snapshot.Formula, second!.Formula);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void QuotedOperatorsAndEscapesRemainLiteralAndOrdinaryInputIsNeverParsed()
    {
        var vm = new MainWindowViewModel { PendingTerm = "AND (literal)" };
        vm.AddTermCommand.Execute(null); Assert.Equal("AND (literal)", Assert.Single(vm.Terms));
        var state = vm.CreateQueryEditor()!;
        state.EditingModeIndex = 1;
        state.ManualFormula = "\"AND\" OR \"()\" OR \"a b\" OR \"a\\\"b\\\\c\"";
        Assert.True(state.TryCapture(out var snapshot));
        Assert.Equal(new[] { "AND", "()", "a b", "a\"b\\c" }, snapshot!.Terms);
        Assert.True(vm.ApplyGroupedQuery(snapshot));
        Assert.Equal(snapshot.Formula, SearchFormula.Format(vm.QueryExpression!, vm.Terms.ToArray()));
    }

    [AvaloniaFact]
    public void ActualRegexControlsValidateIndependentlyAndMainRequestNeverMixesModes()
    {
        var vm = new MainWindowViewModel { PendingTerm = "old", MatchCase = true };
        vm.AddTermCommand.Execute(null);
        var main = new MainWindow { DataContext = vm };
        try
        {
            main.Show(); Settle(main); Click(main, main.FindControl<Button>("GroupingButton")!);
            var dialog = Assert.Single(main.OwnedWindows.OfType<QueryGroupingWindow>()); Settle(dialog);
            var state = Assert.IsType<QueryEditorViewModel>(dialog.DataContext);
            dialog.FindControl<TabControl>("SearchTabs")!.SelectedIndex = 1; Settle(dialog);
            var input = dialog.FindControl<TextBox>("RegexPatternInput")!;
            input.Text = "["; Settle(dialog);
            Assert.False(dialog.FindControl<Button>("ApplyButton")!.IsEnabled);
            Assert.False(state.TryCapture(out _)); Assert.Contains("语法错误", state.RegexError);
            Assert.Equal(state.RegexError, AutomationProperties.GetName(ById<TextBlock>(dialog, "QueryRegexError")));
            input.Text = "  [A-Z]+  "; Settle(dialog);
            Assert.True(dialog.FindControl<Button>("ApplyButton")!.IsEnabled);
            Click(dialog, dialog.FindControl<Button>("ApplyButton")!); Settle(main);
            Assert.True(vm.IsRegularExpression); Assert.Equal("  [A-Z]+  ", vm.QueryRegexPattern);
            Assert.Empty(vm.Terms); Assert.Empty(vm.QueryChips); Assert.False(vm.HasGroupedQuery);
            Assert.False(main.FindControl<TextBox>("KeywordBox")!.IsEnabled);
            Assert.Equal("正则：  [A-Z]+  ", vm.QueryFormula);
            var request = vm.CaptureSearchRequest([".txt"]);
            Assert.Equal(SearchMatchMode.RegularExpression, request.MatchMode); Assert.True(request.MatchCase);
            Assert.Equal("  [A-Z]+  ", request.RegexPattern); Assert.Empty(request.Terms);
            Assert.Null(request.Expression); Assert.Null(request.TermConnectors);
            vm.Terms.Add("leak"); Assert.Empty(vm.Terms);
            var logic = vm.CreateQueryEditor()!;
            Assert.Equal(1, logic.SelectedTabIndex); Assert.Equal(request.RegexPattern, logic.RegexPattern);
            logic.SelectedTabIndex = 0; logic.PendingLiteral = "fresh"; logic.AddToRootCommand.Execute(null);
            Assert.True(logic.TryCapture(out var snapshot)); Assert.True(vm.ApplyGroupedQuery(snapshot!));
            var literal = vm.CaptureSearchRequest([".txt"]);
            Assert.Equal(SearchMatchMode.Literal, literal.MatchMode); Assert.Null(literal.RegexPattern);
            Assert.Equal("fresh", Assert.Single(literal.Terms)); Assert.True(vm.IsLiteralInputEnabled);
        }
        finally { foreach (var owned in main.OwnedWindows.ToArray()) owned.Close(); main.Close(); }
    }

    [AvaloniaFact]
    public void BusyAndPendingInputBlockNewEditorsApplicationsAndAllNewModeEdits()
    {
        var vm = new MainWindowViewModel { PendingTerm = "original" }; vm.AddTermCommand.Execute(null);
        var state = vm.CreateQueryEditor()!;
        state.PendingLiteral = "pending"; state.EditingModeIndex = 1;
        Assert.True(state.IsVisualEditor); Assert.Equal("pending", state.PendingLiteral);
        state.SelectedTabIndex = 1; state.RegexPattern = "good";
        Assert.False(state.CanApply); Assert.False(state.TryCapture(out _));
        state.PendingLiteral = string.Empty; Assert.True(state.TryCapture(out var valid));
        var dialog = new QueryGroupingWindow(state);
        try
        {
            dialog.Show(); Settle(dialog); vm.IsBusy = true; Settle(dialog);
            Assert.False(dialog.FindControl<Button>("ApplyButton")!.IsEnabled);
            Assert.False(dialog.FindControl<TextBox>("RegexPatternInput")!.IsEnabled);
            state.RegexPattern = "changed"; state.SelectedTabIndex = 0; state.ManualFormula = "changed";
            Assert.Equal("good", state.RegexPattern); Assert.Equal(1, state.SelectedTabIndex);
            Assert.False(state.TryCapture(out _)); Assert.False(vm.ApplyGroupedQuery(valid!)); Assert.Null(vm.CreateQueryEditor());
            Assert.Equal("original", Assert.Single(vm.Terms));
            vm.IsBusy = false; vm.PendingTerm = "new pending";
            Assert.False(vm.ApplyGroupedQuery(valid!)); Assert.Equal("new pending", vm.PendingTerm);
        }
        finally { vm.IsBusy = false; dialog.Close(); }
    }

    [AvaloniaFact]
    public void QueriesAndModeDoNotPersistInPreferences()
    {
        using var temp = new TempDirectory();
        var settingsPath = Path.Combine(temp.Root, "settings.json");
        var vm = new MainWindowViewModel(new JsonSettingsStore(settingsPath));
        var state = vm.CreateQueryEditor()!; state.EditingModeIndex = 1;
        state.ManualFormula = "\"PRIVATE_LITERAL_QUERY\"";
        Assert.True(state.TryCapture(out var logic)); Assert.True(vm.ApplyGroupedQuery(logic!)); vm.SaveSettings();
        state = vm.CreateQueryEditor()!; state.SelectedTabIndex = 1; state.RegexPattern = "PRIVATE_REGEX_QUERY[0-9]+";
        Assert.True(state.TryCapture(out var regex)); Assert.True(vm.ApplyGroupedQuery(regex!)); vm.SaveSettings();
        state.ManualFormula = "PRIVATE_MANUAL_DRAFT";
        var saved = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("PRIVATE_LITERAL_QUERY", saved); Assert.DoesNotContain("PRIVATE_REGEX_QUERY", saved);
        Assert.DoesNotContain("PRIVATE_MANUAL_DRAFT", saved); Assert.DoesNotContain("RegexPattern", saved);
        var restored = new MainWindowViewModel(new JsonSettingsStore(settingsPath));
        Assert.Equal(SearchMatchMode.Literal, restored.QueryMatchMode); Assert.Null(restored.QueryRegexPattern);
        Assert.Empty(restored.Terms); Assert.Empty(restored.PendingTerm);
    }

    [AvaloniaFact]
    public async Task RealRegexSearchUsesCaseOptionAndPreviewAndOriginalFindReceiveMatchedText()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "actual.txt");
        File.WriteAllText(path, "left ABC12 right abc34 end");
        var assistant = new FindRecorder();
        var vm = new MainWindowViewModel(findAssistant: assistant) { RootFolder = temp.Root, MatchCase = true };
        var state = vm.CreateQueryEditor()!; state.SelectedTabIndex = 1; state.RegexPattern = "[A-Z]{3}[0-9]{2}";
        Assert.True(state.TryCapture(out var snapshot)); Assert.True(vm.ApplyGroupedQuery(snapshot!));
        await vm.StartSearchCommand.ExecuteAsync(null);
        var file = Assert.Single(vm.Results);
        var firstMatch = Assert.IsType<MatchItem>(file.FirstMatch);
        Assert.Equal(1, file.TotalOccurrences); Assert.Equal("ABC12", firstMatch.Text);
        var request = new MatchPreviewRequest(path, firstMatch, File.GetLastWriteTimeUtc(path), TextEncodingMode.StrictUnicode, new DocumentScopeOptions());
        var preview = await new MatchPreviewLoader().LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(5, preview.SelectionLength);
        Assert.Equal("ABC12", preview.Text.Substring(preview.SelectionStart, preview.SelectionLength));
        await vm.OpenFileCommand.ExecuteAsync(file);
        Assert.Equal("ABC12", Assert.Single(assistant.Requests).SearchText);
        vm.MatchCase = false; await vm.StartSearchCommand.ExecuteAsync(null);
        Assert.Equal(2, Assert.Single(vm.Results).TotalOccurrences);
    }

    [AvaloniaFact]
    public void AcceptedWhitespacePatternRemainsUntrimmedAndClearingLogicDropsOldRegex()
    {
        var vm = new MainWindowViewModel();
        var state = vm.CreateQueryEditor()!; state.SelectedTabIndex = 1; state.RegexPattern = " ";
        Assert.True(state.TryCapture(out var regex)); Assert.True(vm.ApplyGroupedQuery(regex!));
        Assert.Equal(" ", vm.CaptureSearchRequest([".txt"]).RegexPattern);
        state = vm.CreateQueryEditor()!; state.SelectedTabIndex = 0;
        Assert.True(state.TryCapture(out var empty)); Assert.True(vm.ApplyGroupedQuery(empty!));
        Assert.Null(vm.QueryRegexPattern); Assert.False(vm.IsRegularExpression); Assert.Empty(vm.Terms);
    }

    [Fact]
    public void ImportAtExactCoreNodeBudgetAddsNoArtificialRootExpressionNode()
    {
        // 64 leaves and 63 joins plus 129 explicit brackets are exactly 256 nodes.
        var formula = string.Join(" AND ", Enumerable.Range(0, 64).Select(index => index == 0 ? "(((K0)))" : "((K" + index + "))"));
        var parsed = LogicalQueryParser.Parse(formula); Assert.True(parsed.Success, parsed.Error);
        var imported = QueryEditorModel.FromExpression(parsed.Terms, parsed.Expression);
        Assert.Equal(parsed.Formula, imported.Snapshot.Formula);
        var first = imported.GetRows().First();
        Assert.False(imported.TryGroup([first.Id], SearchOperator.And, out var error)); Assert.Contains("256", error);
    }

    private static T ById<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseMove(point.Value); window.MouseDown(point.Value, MouseButton.Left); window.MouseUp(point.Value, MouseButton.Left);
        Settle(window);
    }
    private static void Settle(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private sealed class FindRecorder : IOriginalFileFindAssistant
    {
        public List<FindAssistRequest> Requests { get; } = [];
        public Task<FindAssistResult> FindAsync(FindAssistRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new FindAssistResult(FindAssistStatus.SearchFieldFilled, "test", request.SearchText));
        }
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalDocSearch-advanced-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
