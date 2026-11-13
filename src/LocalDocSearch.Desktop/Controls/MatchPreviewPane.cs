using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LocalDocSearch.Desktop.ViewModels;

namespace LocalDocSearch.Desktop.Controls;

/// <summary>Shared, cancellable extracted-text preview for the sidebar and standalone window.</summary>
public sealed class MatchPreviewPane : UserControl, IDisposable
{
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private readonly TextBlock _fileName;
    private readonly TextBlock _position;
    private readonly Button _open;
    private readonly Button _popOut;
    public bool CanPopOut { get => _popOut.IsVisible; set => _popOut.IsVisible = value; }
    private CancellationTokenSource? _cancellation;
    private MatchPreviewRequest? _request;
    private long _generation;
    private bool _disposed;
    public bool SelectionVerified { get; private set; }
    public Task SelectionTask { get; private set; } = Task.CompletedTask;
    public event EventHandler? CloseRequested;
    public event EventHandler? PopOutRequested;

    public MatchPreviewPane()
    {
        Background = Brush("#FFFFFF");
        var layout = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 0 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 4 };
        var title = new TextBlock { Text = "命中文字预览", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(title);
        var popOut = new Button { Content = "独立窗口", Padding = new(7, 4) };
        _popOut = popOut;
        popOut.Classes.Add("quiet");
        AutomationProperties.SetAutomationId(popOut, "PopOutPreview");
        popOut.Click += (_, _) => PopOutRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(popOut, 1); heading.Children.Add(popOut);
        var close = new Button { Content = "×", Padding = new(7, 4) };
        close.Classes.Add("quiet");
        AutomationProperties.SetAutomationId(close, "CloseInlinePreview");
        AutomationProperties.SetName(close, "关闭文字预览");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 2); heading.Children.Add(close);
        layout.Children.Add(new Border { Child = heading, Padding = new(14, 10), BorderBrush = Brush("#E6EBF2"), BorderThickness = new(0, 0, 0, 1) });

        var source = new StackPanel { Spacing = 5 };
        _fileName = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _position = new TextBlock { FontSize = 12, Foreground = Brush("#718096"), TextWrapping = TextWrapping.Wrap };
        source.Children.Add(_fileName); source.Children.Add(_position);
        var sourceCard = new Border { Child = source, Padding = new(14, 12), Background = Brush("#F8FAFC"), BorderBrush = Brush("#E6EBF2"), BorderThickness = new(0, 0, 0, 1) };
        Grid.SetRow(sourceCard, 1); layout.Children.Add(sourceCard);
        _text = new TextBox
        {
            Name = "PreviewTextBox", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Padding = new(0),
            Background = Brush("#FFFFFF"), BorderThickness = new(0), CornerRadius = new(0),
            SelectionBrush = Brush("#FFF0BB"), SelectionForegroundBrush = Brush("#1E293B"),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(_text, "PreviewTextBox");
        AutomationProperties.SetName(_text, "命中文字，只读预览");
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        var documentLabel = new TextBlock { Text = "提取文字  /  只读", FontSize = 11, Foreground = Brush("#718096"), VerticalAlignment = VerticalAlignment.Top, Margin = new(14, 12, 14, 0) };
        Grid.SetRow(documentLabel, 2); layout.Children.Add(documentLabel);
        _text.Margin = new(14, 34, 14, 14);
        Grid.SetRow(_text, 2); layout.Children.Add(_text);
        _status = new TextBlock { Text = "选择一条匹配以查看文字", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#315CE7") };
        AutomationProperties.SetAutomationId(_status, "PreviewSelectionStatus");
        var footer = new StackPanel { Spacing = 7 };
        footer.Children.Add(_status);
        footer.Children.Add(new TextBlock { Text = "源文件保持只读 · 原排版由阅读器呈现", FontSize = 11, Foreground = Brush("#718096"), TextWrapping = TextWrapping.Wrap });
        _open = new Button { Content = "默认程序打开原文件", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
        _open.Classes.Add("quiet");
        AutomationProperties.SetAutomationId(_open, "PreviewOpenOriginal");
        _open.Click += (_, _) => OpenOriginal();
        footer.Children.Add(_open);
        var footerCard = new Border { Child = footer, Padding = new(14, 10), BorderBrush = Brush("#E6EBF2"), BorderThickness = new(0, 1, 0, 0) };
        Grid.SetRow(footerCard, 3); layout.Children.Add(footerCard);
        Content = layout;
    }

    public Task ShowAsync(MatchPreviewRequest request, MatchPreviewLoader? loader = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Clear();
        _request = request;
        _fileName.Text = Path.GetFileName(request.FullPath);
        ToolTip.SetTip(_fileName, request.FullPath);
        _position.Text = SourcePositionFormatter.Format(request.Match.Location);
        _status.Text = "正在重新读取并核对原始来源…";
        _open.IsEnabled = true;
        _cancellation = new CancellationTokenSource();
        var generation = _generation;
        var token = _cancellation.Token;
        SelectionTask = LoadAsync(request, loader ?? new(), generation, token);
        return SelectionTask;
    }

    private async Task LoadAsync(MatchPreviewRequest request, MatchPreviewLoader loader, long generation, CancellationToken token)
    {
        try
        {
            var data = await Task.Run(() => loader.LoadAsync(request, token), token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return;
            _text.Text = data.Text;
            // Assign the caret before the selection; changing it afterwards collapses the selection.
            _text.CaretIndex = data.SelectionStart + data.SelectionLength;
            _text.SelectionStart = data.SelectionStart;
            _text.SelectionEnd = data.SelectionStart + data.SelectionLength;
            _text.Focus();
            SelectionVerified = _text.SelectedText == request.Match.Text;
            if (!SelectionVerified) throw new IOException("文字控件选区未通过读回核验。");
            _status.Text = $"已选中 {_text.SelectedText.Length} 个字符 · {data.Position}" +
                (data.IsClipped ? " · 仅显示命中附近文字" : "") +
                (data.WarningCount > 0 ? $" · 提取提示 {data.WarningCount} 条" : "");
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && generation == _generation && !token.IsCancellationRequested) _text.Focus();
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != _generation || _disposed) return;
            SelectionVerified = false;
            _text.Text = string.Empty;
            _status.Text = $"定位未完成：{ex.Message}";
        }
    }

    public void Clear()
    {
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _request = null;
        SelectionVerified = false;
        _text.Text = string.Empty;
        _fileName.Text = string.Empty;
        ToolTip.SetTip(_fileName, null);
        _position.Text = string.Empty;
        _status.Text = "选择一条匹配以查看文字";
        _open.IsEnabled = false;
    }

    private void OpenOriginal()
    {
        if (_request is not { } request || _disposed) return;
        try { Process.Start(new ProcessStartInfo(request.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = $"原文件打开失败：{ex.Message}"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
