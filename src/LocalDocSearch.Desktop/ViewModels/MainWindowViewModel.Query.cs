using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

public sealed record QueryTermChip(string Term, string Connector, bool IsStructured = false)
{
    public bool HasConnector => Connector.Length > 0;
    public string EditHint => IsStructured ? $"{Term} · 已使用括号公式；请点“高级搜索…”编辑连接或删除此项。" : Term;
}

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, SearchOperator> _termLinks = new(StringComparer.Ordinal);
    private GroupedQuerySnapshot? _groupedQuery;
    private QueryEditorViewModel? _activeQueryEditor;
    public SearchMatchMode QueryMatchMode { get; private set; } = SearchMatchMode.Literal;
    public string? QueryRegexPattern { get; private set; }
    public bool IsRegularExpression => QueryMatchMode == SearchMatchMode.RegularExpression;
    public bool IsLiteralInputEnabled => IsIdle && !IsRegularExpression;
    private bool _synchronizingGroupedTerms;
    public ObservableCollection<QueryTermChip> QueryChips { get; } = new ResettableObservableCollection<QueryTermChip>();
    public bool HasGroupedQuery => _groupedQuery is not null;
    public bool IsChipEditingEnabled => IsLiteralInputEnabled && !HasGroupedQuery;
    public SearchExpression? QueryExpression => _groupedQuery?.Expression;
    public string QueryChipHint => IsRegularExpression ? "当前为正则匹配；请点“高级搜索…”编辑 pattern，或切回逻辑搜索并应用。" : HasGroupedQuery
        ? "已使用括号公式；词条连接和删除请点“高级搜索…”。新增词会用下一连接追加到根层。"
        : "点击词条删除；点击词条间连接切换 AND / OR。";

    // Terms remain literal strings; operators are independent UI state, never parsed out of a term.
    private SearchOperator[] CaptureTermConnectors() => Terms.Skip(1)
        .Select(term => _termLinks.GetValueOrDefault(term, UseAnd ? SearchOperator.And : SearchOperator.Or)).ToArray();

    public string QueryFormula
    {
        get
        {
            if (IsRegularExpression) return "正则：" + QueryRegexPattern;
            if (_groupedQuery is { } grouped)
            {
                var preview = grouped;
                if (!string.IsNullOrWhiteSpace(PendingTerm))
                {
                    var draft = QueryEditorModel.FromSnapshot(grouped, _resourceLimits);
                    if (draft.TryAddLiteral(PendingTerm, UseAnd ? SearchOperator.And : SearchOperator.Or, null, out _))
                        preview = draft.Snapshot;
                }
                return "公式：" + preview.Formula;
            }
            var terms = Terms.ToList();
            var links = CaptureTermConnectors().ToList();
            var pending = PendingTerm.Trim();
            if (pending.Length > 0 && pending.Length <= _resourceLimits.MaxTermCharacters &&
                terms.Count < _resourceLimits.MaxTerms && !terms.Contains(pending, StringComparer.Ordinal))
            {
                var candidateLinks = links.Concat(terms.Count == 0 ? Array.Empty<SearchOperator>()
                    : new[] { UseAnd ? SearchOperator.And : SearchOperator.Or }).ToArray();
                try
                {
                    _ = new QueryEditorModel(terms.Append(pending).ToArray(), candidateLinks, limits: _resourceLimits);
                    links = candidateLinks.ToList(); terms.Add(pending);
                }
                catch (ArgumentException) { /* A rejected pending candidate never replaces the displayed query. */ }
            }
            return terms.Count == 0 ? "公式：请添加关键词"
                : "公式：" + SearchFormula.Format(terms, links, UseAnd ? SearchOperator.And : SearchOperator.Or);
        }
    }

    public string PendingTermHint => IsRegularExpression ? "正则模式；请在高级搜索中编辑或切回逻辑搜索" : Terms.Count >= _resourceLimits.MaxTerms
        ? $"已达 {_resourceLimits.MaxTerms} 个关键词上限；请先删除一项"
        : Terms.Count == 0 ? "输入第一个关键词后按 Enter"
        : HasGroupedQuery ? $"新词在根层用 {(UseAnd ? "AND" : "OR")} 追加；组内编辑请点高级搜索"
        : $"下一项用 {(UseAnd ? "AND" : "OR")} 连接；输入后按 Enter";

    partial void OnPendingTermChanged(string value) => NotifyQueryFormula();

    private void NotifyQueryFormula()
    {
        OnPropertyChanged(nameof(QueryFormula));
        OnPropertyChanged(nameof(PendingTermHint));
    }

    private void RefreshQueryChips()
    {
        foreach (var stale in _termLinks.Keys.Where(term => !Terms.Contains(term, StringComparer.Ordinal)).ToArray())
            _termLinks.Remove(stale);
        var connectors = CaptureTermConnectors();
        ((ResettableObservableCollection<QueryTermChip>)QueryChips).ReplaceAll(Terms.Select((term, index) =>
            new QueryTermChip(term, HasGroupedQuery || index == 0 ? string.Empty : connectors[index - 1] == SearchOperator.And ? "AND" : "OR", HasGroupedQuery)).ToArray());
        NotifyQueryFormula();
    }

    [RelayCommand]
    private void ToggleTermConnector(string? term)
    {
        if (IsBusy || term is null || Terms.IndexOf(term) <= 0) return;
        if (HasGroupedQuery) { StatusText = QueryChipHint; return; }
        var current = _termLinks.GetValueOrDefault(term, UseAnd ? SearchOperator.And : SearchOperator.Or);
        _termLinks[term] = current == SearchOperator.And ? SearchOperator.Or : SearchOperator.And;
        RefreshQueryChips();
    }

    private void HandleTermsChanged()
    {
        if (!_synchronizingGroupedTerms) RefreshQueryChips();
    }

    // Block stale flat callers before a mutation, rather than restoring inside CollectionChanged
    // (which would be reentrant once the collection has more than one observer).
    private sealed class QueryTermsCollection(Func<bool> isReadOnly, Action rejected) : ObservableCollection<string>
    {
        protected override void InsertItem(int index, string item) { if (isReadOnly()) rejected(); else base.InsertItem(index, item); }
        protected override void SetItem(int index, string item) { if (isReadOnly()) rejected(); else base.SetItem(index, item); }
        protected override void RemoveItem(int index) { if (isReadOnly()) rejected(); else base.RemoveItem(index); }
        protected override void ClearItems() { if (isReadOnly()) rejected(); else base.ClearItems(); }
        protected override void MoveItem(int oldIndex, int newIndex) { if (isReadOnly()) rejected(); else base.MoveItem(oldIndex, newIndex); }
    }

    private void SynchronizeTerms(IReadOnlyList<string> terms)
    {
        _synchronizingGroupedTerms = true;
        try
        {
            Terms.Clear();
            foreach (var term in terms) Terms.Add(term);
        }
        finally { _synchronizingGroupedTerms = false; }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChipEditingEnabled));
        OnPropertyChanged(nameof(IsLiteralInputEnabled));
        _activeQueryEditor?.RefreshAvailability();
    }

    public QueryEditorViewModel? CreateQueryEditor()
    {
        if (IsBusy) { StatusText = "搜索进行中；请在搜索结束后编辑括号公式。"; return null; }
        AddTerm();
        if (!string.IsNullOrWhiteSpace(PendingTerm)) return null;
        try
        {
            var model = _groupedQuery is { } grouped
                ? QueryEditorModel.FromSnapshot(grouped, _resourceLimits)
                : new QueryEditorModel(Terms.ToArray(), CaptureTermConnectors(), UseAnd ? SearchOperator.And : SearchOperator.Or, _resourceLimits);
            return _activeQueryEditor = new QueryEditorViewModel(model, () => IsBusy, QueryMatchMode, QueryRegexPattern, MatchCase);
        }
        catch (ArgumentException ex) { StatusText = ex.Message; return null; }
    }

    public bool ApplyGroupedQuery(GroupedQuerySnapshot snapshot)
    {
        if (IsBusy) { StatusText = "搜索进行中；请在搜索结束后应用括号公式。"; return false; }
        if (!string.IsNullOrWhiteSpace(PendingTerm))
        { StatusText = "主窗口还有待添加的词；请先处理输入，再打开高级搜索。"; return false; }
        try
        {
            if (snapshot.MatchMode == SearchMatchMode.RegularExpression)
            {
                var error = RegularExpressionQuery.Validate(snapshot.RegexPattern, MatchCase, _resourceLimits);
                if (error is not null) { StatusText = error; return false; }
                QueryMatchMode = SearchMatchMode.RegularExpression;
                QueryRegexPattern = snapshot.RegexPattern;
                _groupedQuery = null;
                _termLinks.Clear();
                SynchronizeTerms([]);
                RefreshQueryChips();
                NotifyQueryMode();
                StatusText = "已应用正则匹配；命中显示实际匹配文字。";
                return true;
            }
            if (snapshot.MatchMode != SearchMatchMode.Literal) { StatusText = "请选择逻辑搜索或正则匹配。"; return false; }
            var validated = QueryEditorModel.FromSnapshot(snapshot, _resourceLimits).Snapshot;
            SetGroupedSnapshot(validated);
            StatusText = validated.Terms.Count == 0 ? "公式已清空；请添加关键词后搜索。"
                : "已应用括号公式；新增词追加到根层，连接和删除请在高级搜索中编辑。";
            return true;
        }
        catch (ArgumentException ex) { StatusText = ex.Message; return false; }
    }

    private void SetGroupedSnapshot(GroupedQuerySnapshot snapshot)
    {
        QueryMatchMode = SearchMatchMode.Literal;
        QueryRegexPattern = null;
        _groupedQuery = snapshot.Expression is null ? null : snapshot;
        _termLinks.Clear();
        SynchronizeTerms(snapshot.Terms);
        RefreshQueryChips();
        NotifyQueryMode();
    }

    private void NotifyQueryMode()
    {
        OnPropertyChanged(nameof(QueryMatchMode));
        OnPropertyChanged(nameof(QueryRegexPattern));
        OnPropertyChanged(nameof(IsRegularExpression));
        OnPropertyChanged(nameof(IsLiteralInputEnabled));
        OnPropertyChanged(nameof(HasGroupedQuery));
        OnPropertyChanged(nameof(QueryExpression));
        OnPropertyChanged(nameof(IsChipEditingEnabled));
        OnPropertyChanged(nameof(QueryChipHint));
        NotifyQueryFormula();
    }

    /// <summary>Captures exactly the active mode; pattern and literal expressions never mix.</summary>
    public SearchRequest CaptureSearchRequest(IReadOnlyList<string> extensions)
    {
        var request = new SearchRequest(RootFolder, IsRegularExpression ? Array.Empty<string>() : Terms.ToArray(),
            UseAnd ? SearchOperator.And : SearchOperator.Or, extensions, MatchCase,
            Math.Clamp(ContextCharacters, 0, 500), (TextEncodingMode)Math.Clamp(EncodingIndex, 0, 2));
        if (IsRegularExpression)
        {
            var error = RegularExpressionQuery.Validate(QueryRegexPattern, MatchCase, _resourceLimits);
            if (error is not null) throw new ArgumentException(error);
            return request with { MatchMode = SearchMatchMode.RegularExpression, RegexPattern = QueryRegexPattern };
        }
        return _groupedQuery is { Expression: not null } grouped
            ? request with { Expression = grouped.Expression }
            : request with { TermConnectors = CaptureTermConnectors() };
    }

    private bool TryAddGroupedTerm(string term)
    {
        var draft = QueryEditorModel.FromSnapshot(_groupedQuery!, _resourceLimits);
        if (!draft.TryAddLiteral(term, UseAnd ? SearchOperator.And : SearchOperator.Or, null, out var error))
        { StatusText = error; return false; }
        SetGroupedSnapshot(draft.Snapshot);
        return true;
    }
}
