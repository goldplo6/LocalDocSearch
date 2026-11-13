using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class DesignAdvancedWindowTests
{
    [AvaloniaTheory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void ExpandedHelpKeepsAllVisualActionsAndEditorsInsideTheFixedFooter(double scale)
    {
        var state = new QueryEditorViewModel(new QueryEditorModel(["产品方案", "交付计划", "验收报告"]));
        var window = new QueryGroupingWindow(state);
        try
        {
            window.Show(); Settle(window);
            window.SetRenderScaling(scale); Settle(window);
            foreach (var size in new[] { new Size(720, 600), new Size(860, 720) })
            {
                window.Width = size.Width; window.Height = size.Height; Settle(window);
                var tabs = window.FindControl<TabControl>("SearchTabs")!;
                tabs.SelectedIndex = 0;
                window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 0;
                Settle(window);
                var logicHelp = window.FindControl<Expander>("LogicHelp")!;
                Assert.False(logicHelp.IsExpanded);
                logicHelp.IsExpanded = true; Settle(window);
                var literal = window.FindControl<TextBox>("LiteralInput")!;
                AssertInside(window, literal);
                foreach (var name in new[] { "GroupAndButton", "GroupOrButton", "UngroupButton", "DeleteButton", "ClearSelectionButton", "AddRootButton", "AddGroupButton" })
                    AssertInside(window, window.FindControl<Button>(name)!);
                AssertInside(window, window.FindControl<ComboBox>("EditingMode")!);
                AssertInside(window, window.FindControl<ComboBox>("NextConnector")!);
                AssertInside(window, window.FindControl<ListBox>("NodeRows")!);
                AssertFooterBelow(window, literal);
                AssertHelpScrollLimit(logicHelp);

                window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1; Settle(window);
                var manual = window.FindControl<TextBox>("ManualFormulaInput")!;
                manual.Text = "产品方案 AND (交付计划 OR )"; Settle(window);
                Assert.False(state.CanApply);
                var manualError = ById<TextBlock>(window, "QueryManualError");
                Assert.Equal(state.ManualError, manualError.Text);
                AssertInside(window, manualError);
                AssertFooterBelow(window, manual);
                Assert.False(window.FindControl<Button>("ApplyButton")!.IsEnabled);
                manual.Text = "产品方案 AND (交付计划 OR 验收报告)"; Settle(window);
                Assert.True(state.CanApply);
                logicHelp.IsExpanded = false;

                tabs.SelectedIndex = 1; Settle(window);
                var regexHelp = window.FindControl<Expander>("RegexHelp")!;
                Assert.False(regexHelp.IsExpanded);
                regexHelp.IsExpanded = true; Settle(window);
                var regex = window.FindControl<TextBox>("RegexPatternInput")!;
                regex.Text = "["; Settle(window);
                Assert.False(state.CanApply);
                AssertInside(window, ById<TextBlock>(window, "QueryRegexError"));
                AssertFooterBelow(window, regex);
                AssertHelpScrollLimit(regexHelp);
                regexHelp.IsExpanded = false;
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LongStatusAndErrorsStayScrollableWithoutMovingApplyOutOfTheClient()
    {
        var state = new QueryEditorViewModel(new QueryEditorModel(["产品方案"]));
        var window = new QueryGroupingWindow(state) { Width = 720, Height = 600 };
        try
        {
            window.Show(); Settle(window);
            state.Message = string.Concat(Enumerable.Repeat("请先处理待添加关键词，再应用高级搜索。", 60));
            window.FindControl<ComboBox>("EditingMode")!.SelectedIndex = 1;
            state.ManualFormula = "产品方案 AND ("; Settle(window);
            var message = ById<TextBlock>(window, "QueryGroupMessage");
            var statusScroll = Assert.Single(message.GetVisualAncestors().OfType<ScrollViewer>().Take(1));
            Assert.True(statusScroll.MaxHeight <= 54);
            Assert.True(statusScroll.Extent.Height > statusScroll.Viewport.Height);
            AssertInside(window, statusScroll);
            AssertFooterBelow(window, window.FindControl<TextBox>("ManualFormulaInput")!);
            Assert.Equal(state.ManualError, AutomationProperties.GetName(ById<TextBlock>(window, "QueryManualError")));
            Assert.False(window.FindControl<Button>("ApplyButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    private static void AssertHelpScrollLimit(Expander help)
    {
        var scroll = Assert.IsType<ScrollViewer>(help.Content);
        Assert.InRange(scroll.MaxHeight, 1, 220);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
    }

    private static void AssertFooterBelow(Window window, Control input)
    {
        AssertInside(window, input);
        var cancel = window.FindControl<Button>("CancelButton")!;
        var apply = window.FindControl<Button>("ApplyButton")!;
        AssertInside(window, cancel); AssertInside(window, apply);
        var inputBottom = input.TranslatePoint(new Point(0, input.Bounds.Height), window)!.Value.Y;
        var footerTop = apply.TranslatePoint(default, window)!.Value.Y;
        Assert.True(inputBottom <= footerTop, "An editor overlaps the fixed actions.");
    }

    private static void AssertInside(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(origin.X >= -1 && origin.Y >= -1 &&
                    origin.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
                    origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            $"{control.Name}: {origin} + {control.Bounds.Size} outside {window.ClientSize} at {window.RenderScaling}.");
    }

    private static T ById<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}
