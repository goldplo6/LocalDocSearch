using LocalDocSearch.Core;

namespace LocalDocSearch.Desktop.ViewModels;

internal abstract record QueryNode(Guid Id);
internal sealed record QueryLiteral(Guid Id, string Text) : QueryNode(Id);
internal sealed record QueryGroup(Guid Id, IReadOnlyList<QueryNode> Children,
    IReadOnlyList<SearchOperator> Connectors) : QueryNode(Id);

/// <summary>An immutable, position-based snapshot used by both the formula and the search request.</summary>
public sealed class GroupedQuerySnapshot
{
    internal GroupedQuerySnapshot(QueryGroup root, IReadOnlyList<string> terms, SearchExpression? expression)
    {
        Root = root;
        Terms = Array.AsReadOnly(terms.ToArray());
        Expression = expression;
        Formula = expression is null ? string.Empty : SearchFormula.Format(expression, Terms);
    }

    internal QueryGroup Root { get; }
    public IReadOnlyList<string> Terms { get; }
    public SearchExpression? Expression { get; }
    public string Formula { get; }
    public SearchMatchMode MatchMode { get; private init; } = SearchMatchMode.Literal;
    public string? RegexPattern { get; private init; }

    internal static GroupedQuerySnapshot ForRegularExpression(string pattern) =>
        new(new QueryGroup(Guid.NewGuid(), [], []), [], null)
        { MatchMode = SearchMatchMode.RegularExpression, RegexPattern = pattern };
}

public sealed record QueryEditorNode(Guid Id, Guid ParentId, int Depth, string Text, bool IsGroup,
    SearchOperator? Connector);

/// <summary>
/// A private draft of a literal query. Every operation validates a candidate before replacing the draft.
/// Groups contain ordered children and adjacent joins; removing brackets intentionally rejoins at that level
/// using AND precedence. Identity belongs to a position, so equal literal strings remain distinct terms.
/// </summary>
public sealed class QueryEditorModel
{
    public const int MaximumExpressionDepth = SearchFormula.MaxExpressionDepth;
    public const int MaximumExpressionNodes = SearchFormula.MaxExpressionNodes;
    private readonly SearchResourceLimits _limits;
    private QueryGroup _root;
    private GroupedQuerySnapshot _snapshot;

    public QueryEditorModel(IReadOnlyList<string> terms, IReadOnlyList<SearchOperator>? connectors = null,
        SearchOperator defaultOperator = SearchOperator.And, SearchResourceLimits? limits = null)
    {
        _limits = limits ?? new SearchResourceLimits();
        if (!Enum.IsDefined(defaultOperator)) throw new ArgumentException("连接须为 AND 或 OR。", nameof(defaultOperator));
        if (terms.Count > _limits.MaxTerms) throw new ArgumentException($"关键词数量须不超过 {_limits.MaxTerms}。", nameof(terms));
        var links = connectors?.ToArray() ?? Enumerable.Repeat(defaultOperator, Math.Max(0, terms.Count - 1)).ToArray();
        _root = new QueryGroup(Guid.NewGuid(), terms.Select(text => (QueryNode)new QueryLiteral(Guid.NewGuid(), text)).ToArray(), links);
        if (!TrySerialize(_root, out var snapshot, out var error)) throw new ArgumentException(error, nameof(terms));
        _snapshot = snapshot!;
    }

    private QueryEditorModel(GroupedQuerySnapshot snapshot, SearchResourceLimits limits)
    {
        _limits = limits;
        _root = snapshot.Root;
        if (!TrySerialize(_root, out var copy, out var error)) throw new ArgumentException(error, nameof(snapshot));
        _snapshot = copy!;
    }

    public static QueryEditorModel FromSnapshot(GroupedQuerySnapshot snapshot, SearchResourceLimits? limits = null) =>
        new(snapshot, limits ?? new SearchResourceLimits());
    /// <summary>Imports actual expression leaves as independent editor positions without wrapping every binary node.</summary>
    public static QueryEditorModel FromExpression(IReadOnlyList<string> terms, SearchExpression? expression,
        SearchResourceLimits? limits = null)
    {
        if (expression is null)
        {
            if (terms.Count != 0) throw new ArgumentException("公式须引用全部关键词位置。", nameof(expression));
            return new QueryEditorModel([], limits: limits);
        }
        _ = SearchFormula.Format(expression, terms); // Validate node/depth/position budgets before recursion.
        QueryGroup Sequence(SearchExpression node)
        {
            var children = new List<QueryNode>();
            var connectors = new List<SearchOperator>();
            void Append(SearchExpression current, SearchOperator? join, SearchOperator? parent)
            {
                if (current is BinaryExpression binary && !(parent == SearchOperator.And && binary.Operator == SearchOperator.Or))
                {
                    Append(binary.Left, join, binary.Operator);
                    Append(binary.Right, binary.Operator, binary.Operator);
                    return;
                }
                if (children.Count > 0) connectors.Add(join ?? SearchOperator.And);
                children.Add(current switch
                {
                    TermExpression term => new QueryLiteral(Guid.NewGuid(), terms[term.TermIndex]),
                    GroupExpression group => Sequence(group.Inner),
                    BinaryExpression => Sequence(current),
                    _ => throw new ArgumentException("公式含有未知节点。", nameof(expression))
                });
            }
            Append(node, null, null);
            return new QueryGroup(Guid.NewGuid(), children.ToArray(), connectors.ToArray());
        }
        var root = Sequence(expression);
        return new QueryEditorModel(new GroupedQuerySnapshot(root, terms, expression), limits ?? new SearchResourceLimits());
    }

    internal SearchResourceLimits ResourceLimits => _limits;
    public QueryEditorModel Clone() => new(_snapshot, _limits);
    public GroupedQuerySnapshot Snapshot => _snapshot;
    public Guid RootId => _root.Id;

    public IReadOnlyList<QueryEditorNode> GetRows()
    {
        var rows = new List<QueryEditorNode>();
        void Visit(QueryGroup parent, int depth)
        {
            for (var i = 0; i < parent.Children.Count; i++)
            {
                var node = parent.Children[i];
                rows.Add(new QueryEditorNode(node.Id, parent.Id, depth,
                    node is QueryLiteral literal ? literal.Text : $"括号组（{((QueryGroup)node).Children.Count} 项）",
                    node is QueryGroup, i == 0 ? null : parent.Connectors[i - 1]));
                if (node is QueryGroup group) Visit(group, depth + 1);
            }
        }
        Visit(_root, 0);
        return rows;
    }

    public bool TryAddLiteral(string literal, SearchOperator connector, Guid? parentId, out string error)
    {
        literal = literal.Trim();
        if (literal.Length == 0) { error = "请输入一个字面量关键词。"; return false; }
        if (!Enum.IsDefined(connector)) { error = "连接须为 AND 或 OR。"; return false; }
        var parent = FindGroup(_root, parentId ?? _root.Id);
        if (parent is null) { error = "请选择一个括号组，或添加到根层。"; return false; }
        var updated = parent with
        {
            Children = parent.Children.Append(new QueryLiteral(Guid.NewGuid(), literal)).ToArray(),
            Connectors = parent.Children.Count == 0 ? [] : parent.Connectors.Append(connector).ToArray()
        };
        return TryCommit(ReplaceGroup(_root, parent.Id, updated), out error);
    }

    public bool TryGroup(IReadOnlyCollection<Guid> selection, SearchOperator innerOperator, out string error)
    {
        if (!Enum.IsDefined(innerOperator)) { error = "组内连接须为 AND 或 OR。"; return false; }
        if (!TryLocateSelection(selection, requireAdjacent: true, out var parent, out var indices, out error)) return false;
        var first = indices[0]; var last = indices[^1];
        var group = new QueryGroup(Guid.NewGuid(), parent!.Children.Skip(first).Take(last - first + 1).ToArray(),
            Enumerable.Repeat(innerOperator, last - first).ToArray());
        var updated = parent with
        {
            Children = parent.Children.Take(first).Append(group).Concat(parent.Children.Skip(last + 1)).ToArray(),
            Connectors = parent.Connectors.Take(first).Concat(parent.Connectors.Skip(last)).ToArray()
        };
        return TryCommit(ReplaceGroup(_root, parent.Id, updated), out error);
    }

    public bool TryUngroup(IReadOnlyCollection<Guid> selection, out string error)
    {
        if (selection.Count != 1) { error = "取消括号时请只选择一个括号组。"; return false; }
        if (!TryLocateSelection(selection, requireAdjacent: false, out var parent, out var indices, out error)) return false;
        var index = indices[0];
        if (parent!.Children[index] is not QueryGroup group) { error = "请选择括号组，再取消括号。"; return false; }
        var updated = parent with
        {
            Children = parent.Children.Take(index).Concat(group.Children).Concat(parent.Children.Skip(index + 1)).ToArray(),
            Connectors = parent.Connectors.Take(index).Concat(group.Connectors).Concat(parent.Connectors.Skip(index)).ToArray()
        };
        return TryCommit(ReplaceGroup(_root, parent.Id, updated), out error);
    }

    public bool TryDelete(IReadOnlyCollection<Guid> selection, out string error)
    {
        if (!TryLocateSelection(selection, requireAdjacent: false, out _, out _, out error)) return false;
        var ids = selection.ToHashSet();
        QueryGroup Remove(QueryGroup group)
        {
            var children = new List<QueryNode>(); var joins = new List<SearchOperator>();
            for (var i = 0; i < group.Children.Count; i++)
            {
                var node = group.Children[i];
                if (ids.Contains(node.Id)) continue;
                if (node is QueryGroup nested)
                {
                    var pruned = Remove(nested);
                    if (pruned.Children.Count == 0) continue;
                    node = pruned;
                }
                if (children.Count > 0) joins.Add(group.Connectors[i - 1]);
                children.Add(node);
            }
            return group with { Children = children.ToArray(), Connectors = joins.ToArray() };
        }
        return TryCommit(Remove(_root), out error);
    }

    public bool TrySetConnector(Guid nodeId, SearchOperator connector, out string error)
    {
        if (!Enum.IsDefined(connector)) { error = "连接须为 AND 或 OR。"; return false; }
        var row = GetRows().FirstOrDefault(row => row.Id == nodeId);
        if (row is null || row.Connector is null) { error = "这一项前面没有同层连接。"; return false; }
        var parent = FindGroup(_root, row.ParentId)!;
        var index = parent.Children.ToList().FindIndex(node => node.Id == nodeId);
        var joins = parent.Connectors.ToArray(); joins[index - 1] = connector;
        return TryCommit(ReplaceGroup(_root, parent.Id, parent with { Connectors = joins }), out error);
    }

    private bool TryLocateSelection(IReadOnlyCollection<Guid> selection, bool requireAdjacent,
        out QueryGroup? parent, out int[] indices, out string error)
    {
        parent = null; indices = [];
        if (selection.Count == 0) { error = "请先勾选要编辑的词或括号组。"; return false; }
        var ids = selection.ToHashSet();
        var rows = GetRows().Where(row => ids.Contains(row.Id)).ToArray();
        if (rows.Length != ids.Count || rows.Select(row => row.ParentId).Distinct().Count() != 1)
        { error = "请选择同一层、同一父组的项目；父组和组内项请分开操作。"; return false; }
        parent = FindGroup(_root, rows[0].ParentId)!;
        indices = parent.Children.Select((node, index) => (node, index)).Where(pair => ids.Contains(pair.node.Id))
            .Select(pair => pair.index).ToArray();
        if (requireAdjacent && indices[^1] - indices[0] + 1 != indices.Length)
        { error = "组成组时请选择同层相邻项目；中间的项目也需勾选。"; return false; }
        error = string.Empty;
        return true;
    }

    private bool TryCommit(QueryGroup candidate, out string error)
    {
        if (!TrySerialize(candidate, out var snapshot, out error)) return false;
        _root = candidate; _snapshot = snapshot!;
        return true;
    }

    private bool TrySerialize(QueryGroup root, out GroupedQuerySnapshot? snapshot, out string error)
    {
        snapshot = null;
        try
        {
            var terms = new List<string>();
            var visitedIds = new HashSet<Guid>();
            var draftNodes = 0;
            SearchExpression? Serialize(QueryGroup group, int depth, bool isRoot)
            {
                if (depth > MaximumExpressionDepth || (!isRoot && ++draftNodes > MaximumExpressionNodes) || !visitedIds.Add(group.Id))
                    throw new ArgumentException($"公式最多 {MaximumExpressionDepth} 层、{MaximumExpressionNodes} 个节点；请减少括号或关键词。");
                if (group.Connectors.Count != Math.Max(0, group.Children.Count - 1) || group.Connectors.Any(link => !Enum.IsDefined(link)))
                    throw new ArgumentException("每两个同层项目之间须有一个 AND 或 OR。");
                if (group.Children.Count == 0)
                {
                    if (!isRoot) throw new ArgumentException("括号组内请至少保留一个关键词。");
                    return null;
                }
                var children = new List<SearchExpression>();
                foreach (var node in group.Children)
                {
                    if (node is QueryGroup nested) children.Add(Serialize(nested, depth + 1, false)!);
                    else if (node is QueryLiteral literal)
                    {
                        if (++draftNodes > MaximumExpressionNodes || !visitedIds.Add(literal.Id))
                            throw new ArgumentException($"公式最多 {MaximumExpressionNodes} 个节点。");
                        if (string.IsNullOrWhiteSpace(literal.Text)) throw new ArgumentException("关键词须为非空字面量。");
                        if (literal.Text.Length > _limits.MaxTermCharacters)
                            throw new ArgumentException($"每个关键词长度须不超过 {_limits.MaxTermCharacters} 字符。");
                        if (terms.Count >= _limits.MaxTerms) throw new ArgumentException($"关键词数量须不超过 {_limits.MaxTerms}。");
                        children.Add(new TermExpression(terms.Count)); terms.Add(literal.Text);
                    }
                    else throw new ArgumentException("公式含有未知节点。");
                }
                // At each level, bind consecutive ANDs before joining the resulting runs with OR.
                var runs = new List<SearchExpression>();
                var run = children[0];
                for (var i = 1; i < children.Count; i++)
                {
                    if (group.Connectors[i - 1] == SearchOperator.And) run = new BinaryExpression(SearchOperator.And, run, children[i]);
                    else { runs.Add(run); run = children[i]; }
                }
                runs.Add(run);
                var expression = runs[0];
                for (var i = 1; i < runs.Count; i++) expression = new BinaryExpression(SearchOperator.Or, expression, runs[i]);
                return isRoot ? expression : new GroupExpression(expression);
            }
            var expression = Serialize(root, 0, true);
            if (expression is not null)
            {
                var nodes = 0; var seen = new HashSet<int>();
                var stack = new Stack<(SearchExpression Node, int Depth)>(); stack.Push((expression, 1));
                while (stack.TryPop(out var entry))
                {
                    if (++nodes > MaximumExpressionNodes || entry.Depth > MaximumExpressionDepth)
                        throw new ArgumentException($"公式最多 {MaximumExpressionDepth} 层、{MaximumExpressionNodes} 个节点；此次操作已保留原草稿。");
                    switch (entry.Node)
                    {
                        case TermExpression term:
                            if (!seen.Add(term.TermIndex)) throw new ArgumentException("公式中关键词位置重复。");
                            break;
                        case BinaryExpression binary:
                            stack.Push((binary.Left, entry.Depth + 1)); stack.Push((binary.Right, entry.Depth + 1)); break;
                        case GroupExpression group: stack.Push((group.Inner, entry.Depth + 1)); break;
                    }
                }
                if (seen.Count != terms.Count) throw new ArgumentException("公式须包含每个关键词位置恰好一次。");
            }
            snapshot = new GroupedQuerySnapshot(root, terms, expression);
            error = string.Empty; return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    private static QueryGroup? FindGroup(QueryGroup group, Guid id)
    {
        if (group.Id == id) return group;
        foreach (var child in group.Children.OfType<QueryGroup>())
            if (FindGroup(child, id) is { } found) return found;
        return null;
    }

    private static QueryGroup ReplaceGroup(QueryGroup group, Guid id, QueryGroup replacement) => group.Id == id
        ? replacement
        : group with { Children = group.Children.Select(child => child is QueryGroup nested
            ? ReplaceGroup(nested, id, replacement) : child).ToArray() };
}
