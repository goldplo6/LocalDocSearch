using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LocalDocSearch.Desktop.Controls;

public sealed class ExcerptTextBlock : TextBlock
{
    public static readonly StyledProperty<string> BeforeProperty =
        AvaloniaProperty.Register<ExcerptTextBlock, string>(nameof(Before), string.Empty);
    public static readonly StyledProperty<string> MatchProperty =
        AvaloniaProperty.Register<ExcerptTextBlock, string>(nameof(Match), string.Empty);
    public static readonly StyledProperty<string> AfterProperty =
        AvaloniaProperty.Register<ExcerptTextBlock, string>(nameof(After), string.Empty);

    public string Before { get => GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public string Match { get => GetValue(MatchProperty); set => SetValue(MatchProperty, value); }
    public string After { get => GetValue(AfterProperty); set => SetValue(AfterProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != BeforeProperty && change.Property != MatchProperty && change.Property != AfterProperty)
            return;

        Inlines?.Clear();
        Inlines?.Add(new Run { Text = Before });
        Inlines?.Add(new Run
        {
            Text = $"【{Match}】",
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1E293B")),
            Background = new SolidColorBrush(Color.Parse("#FFF0BB"))
        });
        Inlines?.Add(new Run { Text = After });
    }
}
