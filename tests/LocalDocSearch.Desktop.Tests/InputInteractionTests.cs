using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

// Actual TextBox client/presenter and headless input, not Windows TSF/IMM,
// native candidate windows, or native monitor DPI acceptance.
public sealed class InputInteractionTests
{
    [AvaloniaFact]
    public void RealPreeditEnterDoesNotAddUntilComposedTextAndASeparateEnterArrive()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            var box = window.FindControl<TextBox>("KeywordBox")!;
            Focus(window, box);
            window.KeyTextInput("前"); Settle(window);
            Assert.Equal("前", vm.PendingTerm);
            var client = ActualClient(box);
            var presenter = Assert.Single(box.GetVisualDescendants().OfType<TextPresenter>());
            Assert.Same(presenter, client.TextViewVisual);
            Assert.True(client.SupportsPreedit);
            client.SetPreeditText("zhongwen", 8); Settle(window);
            Assert.Equal("zhongwen", presenter.PreeditText);
            Assert.Equal("前", box.Text);

            Press(window, Key.Enter);
            Assert.Empty(vm.Terms);
            Assert.Equal("前", vm.PendingTerm);
            Assert.Equal("zhongwen", presenter.PreeditText);
            Press(window, Key.ImeProcessed);
            Press(window, Key.None);
            Assert.Empty(vm.Terms);
            Assert.Equal("前", vm.PendingTerm);

            client.SetPreeditText(null); Settle(window);
            window.KeyTextInput("中文"); Settle(window);
            Assert.True(string.IsNullOrEmpty(presenter.PreeditText));
            Assert.Equal("前中文", box.Text);
            Assert.Equal("前中文", vm.PendingTerm);
            Assert.Empty(vm.Terms);
            Press(window, Key.Enter);
            Assert.Equal("前中文", Assert.Single(vm.Terms));
            Assert.Equal(string.Empty, vm.PendingTerm);
            Assert.Equal(string.Empty, box.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CancellingRealPreeditKeepsCommittedTextAndPlainEnterAddsOnlyOnce()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            var box = window.FindControl<TextBox>("KeywordBox")!;
            Focus(window, box);
            window.KeyTextInput("保留"); Settle(window);
            var client = ActualClient(box);
            var presenter = Assert.Single(box.GetVisualDescendants().OfType<TextPresenter>());
            client.SetPreeditText("quxiao", 6); Settle(window);
            Assert.Equal("quxiao", presenter.PreeditText);
            client.SetPreeditText(null); Settle(window);
            Assert.True(string.IsNullOrEmpty(presenter.PreeditText));
            Assert.Equal("保留", vm.PendingTerm);
            Assert.Equal("保留", box.Text);
            Assert.Empty(vm.Terms);
            Press(window, Key.Enter);
            Press(window, Key.Enter);
            Assert.Equal("保留", Assert.Single(vm.Terms));
            Assert.Equal(string.Empty, vm.PendingTerm);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultilineEnterEditsDraftAndTabMovesFocusWithoutEnteringHiddenPage(bool regex)
    {
        var vm = SeedMain();
        var originalFormula = vm.QueryFormula;
        var state = vm.CreateQueryEditor()!;
        var window = new QueryGroupingWindow(state);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Settle(window);
            if (regex) window.FindControl<TabControl>("SearchTabs")!.SelectedIndex = 1;
            else window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1;
            Settle(window);
            var input = window.FindControl<TextBox>(regex ? "RegexPatternInput" : "ManualFormulaInput")!;
            Focus(window, input);
            input.SelectAll();
            window.KeyTextInput(regex ? "(?m)^订单" : "西瓜 AND 苹果"); Settle(window);
            input.CaretIndex = input.Text!.Length;
            var before = input.Text;
            Press(window, Key.Enter);
            Assert.False(closed);
            Assert.Equal(before + input.NewLine, input.Text);
            Assert.Equal(input.Text, regex ? state.RegexPattern : state.ManualFormula);
            Assert.Equal(originalFormula, vm.QueryFormula);
            Assert.True(input.IsFocused);

            Press(window, Key.Tab);
            Assert.False(input.IsFocused);
            Assert.DoesNotContain('\t', input.Text!);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.True(input.IsFocused);
            var visited = new HashSet<Control> { input };
            for (var i = 0; i < 24; i++)
            {
                Press(window, Key.Tab);
                var focused = Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement());
                Assert.True(focused.IsEffectivelyVisible);
                Assert.True(focused.IsEffectivelyEnabled);
                if (focused is TextBox textBox)
                {
                    Assert.NotEqual(regex ? "ManualFormulaInput" : "RegexPatternInput", textBox.Name);
                    Assert.NotEqual("LiteralInput", textBox.Name);
                }
                visited.Add(focused);
            }
            Assert.Contains(window.FindControl<Button>("CancelButton")!, visited);
            Assert.Contains(window.FindControl<Button>("ApplyButton")!, visited);
            Assert.False(closed);
            Assert.Equal(originalFormula, vm.QueryFormula);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EscapeDuringManualPreeditKeepsDialogOpenUntilCompositionIsCleared()
    {
        var vm = SeedMain();
        var originalFormula = vm.QueryFormula;
        var state = vm.CreateQueryEditor()!;
        var window = new QueryGroupingWindow(state);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Settle(window);
            window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(window);
            var input = window.FindControl<TextBox>("ManualFormulaInput")!;
            Focus(window, input);
            input.CaretIndex = input.Text!.Length;
            var committedDraft = input.Text;
            var client = ActualClient(input);
            var presenter = Assert.Single(input.GetVisualDescendants().OfType<TextPresenter>());
            Assert.Same(presenter, client.TextViewVisual);
            client.SetPreeditText("pingguo", 7); Settle(window);
            Assert.Equal("pingguo", presenter.PreeditText);
            Press(window, Key.Escape);
            Assert.False(closed);
            Assert.Equal(committedDraft, input.Text);
            Assert.Equal(committedDraft, state.ManualFormula);
            Assert.Equal(originalFormula, vm.QueryFormula);
            client.SetPreeditText(null); Settle(window);
            Assert.True(string.IsNullOrEmpty(presenter.PreeditText));
            Press(window, Key.Escape);
            Assert.True(closed);
            Assert.Equal(originalFormula, vm.QueryFormula);
            Assert.Equal(new[] { "西瓜", "苹果" }, vm.Terms);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EscapeClosesExpandedEditingModeBeforeCancellingTheDialog()
    {
        var vm = SeedMain();
        var originalFormula = vm.QueryFormula;
        var window = new QueryGroupingWindow(vm.CreateQueryEditor()!);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Settle(window);
            var mode = window.FindControl<ComboBox>("EditingMode")!;
            Focus(window, mode);
            mode.IsDropDownOpen = true; Settle(window);
            Assert.True(mode.IsDropDownOpen);
            Press(window, Key.Escape);
            Assert.False(mode.IsDropDownOpen);
            Assert.False(closed);
            Assert.Equal(originalFormula, vm.QueryFormula);
            Focus(window, mode);
            Press(window, Key.Escape);
            Assert.True(closed);
            Assert.Equal(originalFormula, vm.QueryFormula);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EscapeCancelsTheDraftAndFocusedCancelButtonRespondsToEnter()
    {
        var vm = SeedMain();
        var originalFormula = vm.QueryFormula;
        var state = vm.CreateQueryEditor()!;
        var window = new QueryGroupingWindow(state);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show(); Settle(window);
            window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(window);
            var input = window.FindControl<TextBox>("ManualFormulaInput")!;
            Focus(window, input); input.SelectAll();
            window.KeyTextInput("苹果 OR 梨"); Settle(window);
            Assert.Equal("苹果 OR 梨", state.ManualFormula);
            Assert.Equal(originalFormula, vm.QueryFormula);
            Press(window, Key.Escape);
            Assert.True(closed);
            Assert.Equal(originalFormula, vm.QueryFormula);
            Assert.Equal(new[] { "西瓜", "苹果" }, vm.Terms);
        }
        finally { window.Close(); }

        var second = new QueryGroupingWindow(vm.CreateQueryEditor()!);
        var secondClosed = false;
        second.Closed += (_, _) => secondClosed = true;
        try
        {
            second.Show(); Settle(second);
            Focus(second, second.FindControl<Button>("CancelButton")!);
            Press(second, Key.Enter);
            Assert.True(secondClosed);
            Assert.Equal(originalFormula, vm.QueryFormula);
        }
        finally { second.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void MainWindowMinimumAndDefaultLayoutsKeepInputActionsAndResultsVisibleAtScale(double scale)
    {
        var vm = new MainWindowViewModel();
        for (var i = 0; i < 64; i++) vm.Terms.Add(new string('词', 100) + i);
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show(); Settle(window);
            window.SetRenderScaling(scale); Settle(window);
            foreach (var size in new[] { new Size(window.MinWidth, window.MinHeight), new Size(1140, 820) })
            {
                window.Width = size.Width; window.Height = size.Height; Settle(window);
                Assert.Equal(scale, window.RenderScaling);
                AssertInsideClient(window, window.FindControl<TextBox>("KeywordBox")!);
                AssertInsideClient(window, window.FindControl<Button>("GroupingButton")!);
                var search = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "开始搜索"));
                AssertInsideClient(window, search);
                var results = window.FindControl<ListBox>("ResultsList")!;
                AssertInsideClient(window, results);
                var ratio = results.Bounds.Width * results.Bounds.Height / (window.ClientSize.Width * window.ClientSize.Height);
                Assert.True(ratio >= .60, $"Result area {ratio:P2} at {size}, scaling {scale}");
                Assert.Equal(64, vm.Terms.Count);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void AdvancedPagesKeepEditorsAndActionsInsideMinimumAndDefaultLayoutsAtScale(double scale)
    {
        var state = SeedMain().CreateQueryEditor()!;
        var window = new QueryGroupingWindow(state);
        try
        {
            window.Show(); Settle(window);
            window.SetRenderScaling(scale); Settle(window);
            foreach (var size in new[] { new Size(window.MinWidth, window.MinHeight), new Size(860, 720) })
            {
                window.Width = size.Width; window.Height = size.Height; Settle(window);
                for (var page = 0; page < 3; page++)
                {
                    SelectPage(window, page);
                    var input = window.FindControl<TextBox>(page == 0 ? "LiteralInput" : page == 1 ? "ManualFormulaInput" : "RegexPatternInput")!;
                    input.Text = page == 0 ? new string('词', 80) : page == 1 ? "西瓜 AND (苹果 OR 荔枝)\nAND 梨" : "(?m)^[订单编号]+\\d{2,4}$\n[";
                    Settle(window);
                    Assert.Equal(scale, window.RenderScaling);
                    AssertInsideClient(window, input);
                    var cancel = window.FindControl<Button>("CancelButton")!;
                    var apply = window.FindControl<Button>("ApplyButton")!;
                    AssertInsideClient(window, cancel);
                    AssertInsideClient(window, apply);
                    var inputBottom = input.TranslatePoint(new Point(0, input.Bounds.Height), window)!.Value.Y;
                    var actionTop = apply.TranslatePoint(default, window)!.Value.Y;
                    Assert.True(inputBottom <= actionTop + 1, "Editor overlaps the dialog actions.");
                    Focus(window, input);
                    Press(window, Key.Tab);
                    Assert.False(input.IsFocused);
                    Assert.True(Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement()).IsEffectivelyVisible);
                    // A pending visual literal intentionally blocks changing editing mode.
                    if (page == 0) { input.Clear(); Settle(window); }
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DynamicScalingKeepsMainAndOwnedEditorTextSelectionAndFocus()
    {
        var vm = SeedMain();
        var main = new MainWindow { DataContext = vm };
        QueryGroupingWindow? editor = null;
        try
        {
            main.Show(); Settle(main);
            var state = vm.CreateQueryEditor()!;
            editor = new QueryGroupingWindow(state);
            editor.Show(main); Settle(editor);
            SelectPage(editor, 1);
            var input = editor.FindControl<TextBox>("ManualFormulaInput")!;
            Focus(editor, input); input.SelectAll();
            editor.KeyTextInput("西瓜 AND 苹果"); Settle(editor);
            input.SelectionStart = 0; input.SelectionEnd = 2;
            foreach (var scale in new[] { 1d, 1.5d, 2d, 1d })
            {
                main.SetRenderScaling(scale); Settle(main);
                editor.SetRenderScaling(scale); Settle(editor);
                Assert.Equal(scale, main.RenderScaling);
                Assert.Equal(scale, editor.RenderScaling);
                Assert.Equal("西瓜 AND 苹果", input.Text);
                Assert.Equal("西瓜 AND 苹果", state.ManualFormula);
                Assert.Equal("西瓜", input.SelectedText);
                Assert.True(input.IsFocused);
                AssertInsideClient(main, main.FindControl<TextBox>("KeywordBox")!);
                AssertInsideClient(editor, input);
                AssertInsideClient(editor, editor.FindControl<Button>("ApplyButton")!);
            }
            Assert.Equal(new[] { "西瓜", "苹果" }, vm.Terms);
        }
        finally { editor?.Close(); main.Close(); }
    }

    private static MainWindowViewModel SeedMain()
    {
        var vm = new MainWindowViewModel();
        foreach (var term in new[] { "西瓜", "苹果" }) { vm.PendingTerm = term; vm.AddTermCommand.Execute(null); }
        return vm;
    }

    private static TextInputMethodClient ActualClient(TextBox box)
    {
        var request = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent
        };
        box.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }

    private static void SelectPage(QueryGroupingWindow window, int page)
    {
        window.FindControl<TabControl>("SearchTabs")!.SelectedIndex = page == 2 ? 1 : 0; Settle(window);
        if (page != 2) window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = page == 1 ? 1 : 0;
        Settle(window);
    }

    private static void Focus(Window window, Control control)
    {
        Assert.True(control.Focus()); Settle(window);
        Assert.True(control.IsFocused);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var closed = false;
        void OnClosed(object? sender, EventArgs args) => closed = true;
        window.Closed += OnClosed;
        try
        {
            window.KeyPress(key, modifiers, PhysicalKey.None, null);
            if (!closed) window.KeyRelease(key, modifiers, PhysicalKey.None, null);
            if (!closed) Settle(window);
        }
        finally { window.Closed -= OnClosed; }
    }

    private static void AssertInsideClient(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.Name} is hidden.");
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{control.Name} has no arranged area.");
        var origin = control.TranslatePoint(default, window);
        Assert.NotNull(origin);
        const double tolerance = 1;
        Assert.True(origin.Value.X >= -tolerance && origin.Value.Y >= -tolerance &&
                    origin.Value.X + control.Bounds.Width <= window.ClientSize.Width + tolerance &&
                    origin.Value.Y + control.Bounds.Height <= window.ClientSize.Height + tolerance,
            $"{control.Name}: {origin.Value} + {control.Bounds.Size} outside client {window.ClientSize}, scale {window.RenderScaling}");
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}
