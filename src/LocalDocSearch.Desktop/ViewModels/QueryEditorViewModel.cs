using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class QueryEditorRowViewModel : ObservableObject
{
    private readonly QueryEditorViewModel _owner;
    private bool _restoringConnector;
    internal QueryEditorRowViewModel(QueryEditorViewModel owner, QueryEditorNode node, int ordinal)
    {
        _owner = owner; Node = node;
        connectorIndex = node.Connector == SearchOperator.Or ? 1 : 0;
        Ordinal = ordinal;
    }

    public QueryEditorNode Node { get; }
    public Guid Id => Node.Id;
    public int Ordinal { get; }
    public string Text => Node.Text;
    public bool IsGroup => Node.IsGroup;
    public string Kind => IsGroup ? "（组）" : "词";
    public bool HasConnector => Node.Connector is not null;
    public Thickness Indent => new(Node.Depth * 20, 0, 0, 0);
    public string SelectionId => "QueryNodeSelect_" + Id.ToString("N");
    public string ConnectorId => "QueryNodeConnector_" + Id.ToString("N");
    public string AccessibleName => $"第 {Ordinal} 项，{(IsGroup ? "括号组" : "关键词")}，{Text}，层级 {Node.Depth + 1}";
    public string ConnectorName => $"{AccessibleName}前的同层连接";
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private int connectorIndex;
    partial void OnIsSelectedChanged(bool value) => _owner.UpdateSelection(Id, value);
    partial void OnConnectorIndexChanged(int value)
    {
        if (!_restoringConnector) _owner.SetConnector(Id, value);
    }
    internal void RestoreConnector(SearchOperator connector)
    {
        _restoringConnector = true;
        try { ConnectorIndex = connector == SearchOperator.Or ? 1 : 0; }
        finally { _restoringConnector = false; }
    }
}

/// <summary>Dialog state owns a clone. Closing/cancelling never writes to the main query.</summary>
public sealed partial class QueryEditorViewModel : ObservableObject
{
    private readonly Func<bool> _isBusy;
    private readonly HashSet<Guid> _selected = [];
    private bool _refreshingRows;
    public QueryEditorViewModel(QueryEditorModel model, Func<bool>? isBusy = null,
        SearchMatchMode matchMode = SearchMatchMode.Literal, string? pattern = null, bool matchCase = false)
    {
        Model = model.Clone();
        _isBusy = isBusy ?? (() => false);
        _matchCase = matchCase;
        _regexPattern = pattern ?? string.Empty;
        _selectedTabIndex = matchMode == SearchMatchMode.RegularExpression ? 1 : 0;
        RefreshRows();
        ValidateRegex();
    }

    public QueryEditorModel Model { get; private set; }
    public ObservableCollection<QueryEditorRowViewModel> Rows { get; } = [];
    public static IReadOnlyList<string> OperatorNames { get; } = ["AND", "OR"];
    public IReadOnlyList<string> OperatorChoices => OperatorNames;
    public bool IsEditable => !_isBusy();
    public string Formula => IsManualEditor
        ? (_parsed?.Success == true ? _parsed.Formula : "公式有误；请修正后应用")
        : Model.Snapshot.Expression is null ? "请添加关键词" : Model.Snapshot.Formula;
    public string CountText => $"{Model.Snapshot.Terms.Count} 个词 · {Rows.Count(row => row.IsGroup)} 个括号组";
    public string SelectionText => $"已选 {_selected.Count} 项";
    [ObservableProperty] private string pendingLiteral = string.Empty;
    [ObservableProperty] private int nextConnectorIndex;
    [ObservableProperty] private string message = "勾选同层相邻项组成组；单选一个组可继续嵌套或取消括号。取消括号后，同层 AND 优先于 OR。";

    internal void UpdateSelection(Guid id, bool selected)
    {
        if (selected) _selected.Add(id); else _selected.Remove(id);
        OnPropertyChanged(nameof(SelectionText));
    }

    internal void SetConnector(Guid id, int value)
    {
        // Avalonia clears SelectedIndex to -1 while tearing down the ComboBox. That is a
        // control lifecycle event, not a query edit. Never rebuild Rows from SelectionChanged:
        // the selection model may still be enumerating that very collection (also on Close).
        if (_refreshingRows || value is not (0 or 1)) return;
        if (!EnsureEditable()) { RestoreConnectorLater(id); return; }
        var success = Model.TrySetConnector(id, value == 0 ? SearchOperator.And : SearchOperator.Or, out var error);
        Message = success ? "已修改同层连接；预览就是实际提交的公式。" : error;
        if (success) SynchronizeVisualFormula();
        else RestoreConnectorLater(id);
    }

    private void RestoreConnectorLater(Guid id) => Dispatcher.UIThread.Post(() =>
    {
        var current = Model.GetRows().FirstOrDefault(node => node.Id == id);
        var row = Rows.FirstOrDefault(item => item.Id == id);
        if (current?.Connector is { } connector && row is not null) row.RestoreConnector(connector);
    });

    [RelayCommand] private void GroupAnd() => Group(SearchOperator.And);
    [RelayCommand] private void GroupOr() => Group(SearchOperator.Or);
    private void Group(SearchOperator connector)
    {
        if (!EnsureEditable()) return;
        var success = Model.TryGroup(_selected.ToArray(), connector, out var error);
        Complete(success, error, $"已组成 {connector.ToString().ToUpperInvariant()} 括号组。可选新组继续嵌套，或编辑组内连接。");
    }

    [RelayCommand]
    private void Ungroup()
    {
        if (!EnsureEditable()) return;
        var success = Model.TryUngroup(_selected.ToArray(), out var error);
        Complete(success, error, "已取消所选括号；同层按 AND 优先于 OR 重新计算，请核对预览。");
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (!EnsureEditable()) return;
        var success = Model.TryDelete(_selected.ToArray(), out var error);
        Complete(success, error, "已删除所选项；空括号组已一并移除。");
    }

    [RelayCommand] private void AddToRoot() => AddLiteral(null);
    [RelayCommand]
    private void AddToSelectedGroup()
    {
        if (!EnsureEditable()) return;
        var group = Rows.FirstOrDefault(row => row.IsGroup && _selected.Contains(row.Id));
        if (_selected.Count != 1 || group is null) { Message = "添加到组内时，请只勾选一个括号组。"; return; }
        AddLiteral(group.Id);
    }

    private void AddLiteral(Guid? parentId)
    {
        if (!EnsureEditable()) return;
        if (NextConnectorIndex is not (0 or 1)) { Message = "下一连接须为 AND 或 OR。"; return; }
        var success = Model.TryAddLiteral(PendingLiteral, NextConnectorIndex == 0 ? SearchOperator.And : SearchOperator.Or, parentId, out var error);
        if (success) PendingLiteral = string.Empty;
        Complete(success, error, "已添加独立字面量；重复词也保留独立位置。");
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Rows) row.IsSelected = false;
    }

    public bool TryCapture(out GroupedQuerySnapshot? snapshot)
    {
        snapshot = null;
        if (!EnsureEditable()) return false;
        // Applying must not silently discard text still in the editor's input.
        if (!string.IsNullOrWhiteSpace(PendingLiteral))
        {
            Message = "输入框还有待添加的词；请先选择添加到根层或所选组，再应用。";
            return false;
        }
        if (SelectedTabIndex == 1)
        {
            ValidateRegex();
            if (RegexError.Length != 0) { Message = RegexError; return false; }
            snapshot = GroupedQuerySnapshot.ForRegularExpression(RegexPattern);
            return true;
        }
        if (IsManualEditor && !TryImportManual()) return false;
        snapshot = Model.Snapshot;
        return true;
    }

    private bool EnsureEditable()
    {
        RefreshAvailability();
        if (IsEditable) return true;
        Message = "搜索进行中；请在搜索结束后编辑或应用高级搜索。";
        return false;
    }

    private void Complete(bool success, string error, string successMessage)
    {
        Message = success ? successMessage : error;
        if (success) { _selected.Clear(); RefreshRows(); }
        else RefreshRows(preserveSelection: true);
    }

    private void RefreshRows(bool preserveSelection = false)
    {
        var selected = preserveSelection ? _selected.ToHashSet() : new HashSet<Guid>();
        _refreshingRows = true;
        try
        {
            _selected.Clear(); Rows.Clear();
            var ordinal = 0;
            foreach (var node in Model.GetRows())
            {
                var row = new QueryEditorRowViewModel(this, node, ++ordinal);
                Rows.Add(row);
                if (selected.Contains(row.Id)) row.IsSelected = true;
            }
        }
        finally { _refreshingRows = false; }
        OnPropertyChanged(nameof(Formula));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsEditable));
        SynchronizeVisualFormula();
    }
}
