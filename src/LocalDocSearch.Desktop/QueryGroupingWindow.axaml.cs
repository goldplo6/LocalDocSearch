using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop;

public partial class QueryGroupingWindow : Window
{
    public QueryGroupingWindow() => InitializeComponent();

    public QueryGroupingWindow(QueryEditorViewModel editor) : this() => DataContext = editor;

    private void Apply_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryEditorViewModel editor && editor.TryCapture(out var snapshot)) Close(snapshot);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void ManualMode_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryEditorViewModel editor) editor.EditingModeIndex = 1;
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (FocusManager?.GetFocusedElement() is TextBox box &&
            box.GetVisualDescendants().OfType<TextPresenter>()
                .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText))) return;
        var openChoice = this.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(choice => choice.IsDropDownOpen);
        if (openChoice is not null)
        {
            openChoice.IsDropDownOpen = false;
            e.Handled = true;
            return;
        }
        e.Handled = true;
        Close();
    }
}
