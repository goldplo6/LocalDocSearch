using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalDocSearch.Desktop;
using LocalDocSearch.Desktop.Controls;
using LocalDocSearch.Desktop.ViewModels;
using Xunit;

namespace LocalDocSearch.Desktop.Tests;

public sealed class DemoAlignmentAdvancedTests
{
    [AvaloniaFact]
    public void ActualHeaderAndNativeTabsUseTheDemoBrandAndBlueUnderline()
    {
        var window = CreateWindow();
        try
        {
            window.Show(); Settle(window);
            var header = ById<Border>(window, "AdvancedDialogHeader");
            var brand = ById<BrandMark>(window, "AdvancedDialogBrandMark");
            Assert.True(brand.IsEffectivelyVisible);
            Assert.Contains(header, brand.GetVisualAncestors());
            Assert.Equal(Color.Parse("#FFFFFF"), BrushColor(window.Background));

            var tabs = window.FindControl<TabControl>("SearchTabs")!;
            var items = tabs.Items.OfType<TabItem>().ToArray();
            Assert.Equal(new[] { "逻辑搜索", "正则匹配" }, items.Select(tab => (string)tab.Header!));
            Assert.Equal("AdvancedSearchTabs", AutomationProperties.GetAutomationId(tabs));
            for (var index = 0; index < items.Length; index++)
            {
                tabs.SelectedIndex = index; Settle(window);
                var selected = items[index];
                Assert.NotNull(selected.Theme);
                Assert.Equal(Color.Parse("#315CE7"), BrushColor(selected.Foreground));
                var underline = Assert.Single(selected.GetVisualDescendants().OfType<Border>(), border => border.Name == "TabUnderline");
                Assert.Equal(Color.Parse("#315CE7"), BrushColor(underline.Background));
                Assert.Equal(2d, underline.Bounds.Height);
                Assert.True(underline.Bounds.Width > 30);
                Assert.Contains(selected.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), path => path.Data is not null && path.Stroke is not null);
                AssertInside(window, selected);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void SegmentedButtonsShareTheRealComboBoxGuardsAndCodeCapsStayAboveTheFixedFooter(double scale)
    {
        var window = CreateWindow();
        var state = Assert.IsType<QueryEditorViewModel>(window.DataContext);
        try
        {
            window.Show(); Settle(window);
            window.SetRenderScaling(scale); Settle(window);
            foreach (var size in new[] { new Size(720, 600), new Size(860, 720) })
            {
                window.Width = size.Width; window.Height = size.Height;
                window.FindControl<TabControl>("SearchTabs")!.SelectedIndex = 0; Settle(window);
                var mode = window.FindControl<ComboBox>("EditingMode")!;
                var visual = ById<Button>(window, "QueryVisualMode");
                var manual = ById<Button>(window, "QueryManualMode");
                AssertInside(window, mode);
                AssertInside(window, visual); AssertInside(window, manual);
                Assert.True(mode.IsEffectivelyVisible);

                window.FindControl<TextBox>("LiteralInput")!.Text = "保留待添加文字"; Settle(window);
                Click(window, manual);
                Assert.True(state.IsVisualEditor);
                Assert.Equal(0, mode.SelectedIndex);
                Assert.Equal("保留待添加文字", state.PendingLiteral);
                Assert.Contains("selected", visual.Classes);
                Assert.DoesNotContain("selected", manual.Classes);

                window.FindControl<TextBox>("LiteralInput")!.Clear(); Settle(window);
                Click(window, manual);
                Assert.True(state.IsManualEditor);
                Assert.Equal(1, mode.SelectedIndex);
                Assert.Contains("selected", manual.Classes);
                AssertEditorCapAndFooter(window, "ManualFormulaInput", "QueryLogicEditorCap", "LOGICAL EXPRESSION");

                var formula = window.FindControl<TextBox>("ManualFormulaInput")!;
                formula.Text = "产品方案 AND ("; Settle(window);
                Click(window, visual);
                Assert.True(state.IsManualEditor);
                Assert.Equal(1, mode.SelectedIndex);
                Assert.Contains("selected", manual.Classes);
                Assert.False(window.FindControl<Button>("ApplyButton")!.IsEnabled);
                Assert.Equal(state.ManualError, ById<TextBlock>(window, "QueryManualError").Text);

                formula.Text = "产品方案 AND 交付计划"; Settle(window);
                Click(window, visual);
                Assert.True(state.IsVisualEditor);
                Assert.Equal(0, mode.SelectedIndex);
                Assert.Contains("selected", visual.Classes);

                window.FindControl<TabControl>("SearchTabs")!.SelectedIndex = 1; Settle(window);
                AssertEditorCapAndFooter(window, "RegexPatternInput", "QueryRegexEditorCap", "REGULAR EXPRESSION");
                window.FindControl<TextBox>("RegexPatternInput")!.Text = "["; Settle(window);
                Assert.False(window.FindControl<Button>("ApplyButton")!.IsEnabled);
                Assert.Equal(state.RegexError, ById<TextBlock>(window, "QueryRegexError").Text);
            }
        }
        finally { window.Close(); }
    }

    private static QueryGroupingWindow CreateWindow() =>
        new(new QueryEditorViewModel(new QueryEditorModel(["产品方案", "交付计划", "验收报告"])));

    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static void AssertEditorCapAndFooter(Window window, string inputName, string capId, string label)
    {
        var input = window.FindControl<TextBox>(inputName)!;
        var cap = ById<Border>(window, capId);
        var footer = ById<Border>(window, "QueryGroupFooter");
        AssertInside(window, input); AssertInside(window, cap); AssertInside(window, footer);
        Assert.Contains(cap.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == label);
        Assert.True(cap.Bounds.Height >= 31);
        var inputTop = input.TranslatePoint(default, window)!.Value.Y;
        var capBottom = cap.TranslatePoint(new Point(0, cap.Bounds.Height), window)!.Value.Y;
        var inputBottom = input.TranslatePoint(new Point(0, input.Bounds.Height), window)!.Value.Y;
        var footerTop = footer.TranslatePoint(default, window)!.Value.Y;
        Assert.True(capBottom <= inputTop + 1);
        Assert.True(inputBottom <= footerTop + 1);
        AssertInside(window, window.FindControl<Button>("CancelButton")!);
        AssertInside(window, window.FindControl<Button>("ApplyButton")!);
    }

    private static T ById<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void AssertInside(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(origin.X >= -1 && origin.Y >= -1 &&
                    origin.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
                    origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
    }

    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsEffectivelyEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
    }
}
