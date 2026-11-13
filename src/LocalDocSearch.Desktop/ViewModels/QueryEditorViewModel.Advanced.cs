using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed partial class QueryEditorViewModel
{
    private readonly bool _matchCase;
    private int _selectedTabIndex;
    private int _editingModeIndex;
    private string _manualFormula = string.Empty;
    private string _manualError = string.Empty;
    private int _manualErrorOffset = -1;
    private string _regexPattern = string.Empty;
    private string _regexError = string.Empty;
    private LogicalQueryParseResult? _parsed;

    public IReadOnlyList<string> EditingModes { get; } = ["可视化", "手写公式"];
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value is not (0 or 1) || value == _selectedTabIndex) return;
            if (!EnsureEditable()) { OnPropertyChanged(); return; }
            SetProperty(ref _selectedTabIndex, value);
            NotifyAdvancedState();
        }
    }
    public int EditingModeIndex
    {
        get => _editingModeIndex;
        set
        {
            if (value is not (0 or 1) || value == _editingModeIndex) return;
            if (!EnsureEditable()) { OnPropertyChanged(); return; }
            if (value == 1 && !string.IsNullOrWhiteSpace(PendingLiteral))
            {
                Message = "输入框还有待添加的词；请先添加或清空输入，再切换手写公式。";
                OnPropertyChanged();
                return;
            }
            if (value == 0 && !TryImportManual())
            {
                OnPropertyChanged();
                return;
            }
            if (value == 1) _manualFormula = Model.Snapshot.Formula;
            SetProperty(ref _editingModeIndex, value);
            if (value == 1) ValidateManual();
            NotifyAdvancedState();
        }
    }
    public bool IsVisualEditor => EditingModeIndex == 0;
    public bool IsManualEditor => EditingModeIndex == 1;
    public string ManualFormula
    {
        get => _manualFormula;
        set
        {
            if (!EnsureEditable()) { OnPropertyChanged(); return; }
            if (SetProperty(ref _manualFormula, value ?? string.Empty)) ValidateManual();
        }
    }
    public string ManualError => _manualError;
    public int ManualErrorOffset => _manualErrorOffset;
    public string RegexPattern
    {
        get => _regexPattern;
        set
        {
            if (!EnsureEditable()) { OnPropertyChanged(); return; }
            if (SetProperty(ref _regexPattern, value ?? string.Empty)) ValidateRegex();
        }
    }
    public string RegexError => _regexError;
    public bool CanApply => IsEditable && string.IsNullOrWhiteSpace(PendingLiteral) &&
        (SelectedTabIndex == 1 ? RegexError.Length == 0 : !IsManualEditor || _parsed?.Success == true && ManualError.Length == 0);

    private void ValidateManual()
    {
        _parsed = LogicalQueryParser.Parse(_manualFormula, Model.ResourceLimits);
        _manualError = _parsed.Success ? string.Empty : _parsed.Error;
        _manualErrorOffset = _parsed.ErrorOffset;
        NotifyAdvancedState();
    }

    private void ValidateRegex()
    {
        _regexError = RegularExpressionQuery.Validate(_regexPattern, _matchCase, Model.ResourceLimits) ?? string.Empty;
        NotifyAdvancedState();
    }

    private bool TryImportManual()
    {
        ValidateManual();
        if (_parsed?.Success != true) { Message = ManualError; return false; }
        try
        {
            Model = QueryEditorModel.FromExpression(_parsed.Terms, _parsed.Expression, Model.ResourceLimits);
            _selected.Clear();
            RefreshRows();
            Message = "已将手写公式转换为可视化项目；重复词保留独立位置。";
            return true;
        }
        catch (ArgumentException ex)
        {
            _manualError = ex.Message;
            Message = _manualError;
            OnPropertyChanged(nameof(ManualError));
            OnPropertyChanged(nameof(CanApply));
            return false;
        }
    }

    [RelayCommand] private void UseVisualEditor() => EditingModeIndex = 0;

    private void SynchronizeVisualFormula()
    {
        if (IsVisualEditor) _manualFormula = Model.Snapshot.Formula;
        NotifyAdvancedState();
    }

    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(CanApply));
    }

    private void NotifyAdvancedState()
    {
        OnPropertyChanged(nameof(IsVisualEditor));
        OnPropertyChanged(nameof(IsManualEditor));
        OnPropertyChanged(nameof(ManualFormula));
        OnPropertyChanged(nameof(ManualError));
        OnPropertyChanged(nameof(ManualErrorOffset));
        OnPropertyChanged(nameof(RegexError));
        OnPropertyChanged(nameof(Formula));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnPendingLiteralChanged(string value) => OnPropertyChanged(nameof(CanApply));
}
