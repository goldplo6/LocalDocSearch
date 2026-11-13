using System.Text;

namespace LocalDocSearch.Core;

/// <summary>Builds, formats and evaluates one expression tree. Keywords are literal; no text is parsed.</summary>
public static class SearchFormula
{
    /// <summary>Root depth is one; explicit groups also consume depth and node budgets.</summary>
    public const int MaxExpressionDepth = 128;
    public const int MaxExpressionNodes = 256;

    /// <summary>Builds adjacent keyword joins with AND precedence. Empty input has no expression.</summary>
    public static SearchExpression? CreateExpression(int termCount, IReadOnlyList<SearchOperator>? connectors = null,
        SearchOperator defaultOperator = SearchOperator.And) =>
        CreateSnapshot(termCount, connectors, defaultOperator)?.Expression;

    /// <summary>Formats AND groups before OR, quoting literal terms where needed. Empty input displays as empty text.</summary>
    public static string Format(IReadOnlyList<string> terms, IReadOnlyList<SearchOperator>? connectors = null,
        SearchOperator defaultOperator = SearchOperator.And)
    {
        var snapshot = SnapshotTerms(terms);
        var expression = CreateSnapshot(snapshot.Length, connectors, defaultOperator);
        return expression is null ? string.Empty : FormatValidated(expression.Expression, snapshot);
    }

    /// <summary>Formats a validated tree, preserving every explicit group and binary operator semantics.</summary>
    public static string Format(SearchExpression expression, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var snapshot = SnapshotTerms(terms);
        var validated = SnapshotExpression(expression, snapshot.Length);
        return FormatValidated(validated.Expression, snapshot);
    }

    /// <summary>Evaluates original-position term presence with AND precedence. Empty input evaluates false.</summary>
    public static bool Evaluate(IReadOnlyList<bool> termMatches, IReadOnlyList<SearchOperator>? connectors = null,
        SearchOperator defaultOperator = SearchOperator.And)
    {
        ArgumentNullException.ThrowIfNull(termMatches);
        ValidateTermCount(termMatches.Count);
        var snapshot = termMatches.ToArray();
        var expression = CreateSnapshot(snapshot.Length, connectors, defaultOperator);
        return expression is not null && EvaluateValidated(expression, snapshot);
    }

    /// <summary>Evaluates a tree whose leaves use every original input position exactly once.</summary>
    public static bool Evaluate(SearchExpression expression, IReadOnlyList<bool> termMatches)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(termMatches);
        ValidateTermCount(termMatches.Count);
        var snapshot = termMatches.ToArray();
        return EvaluateValidated(SnapshotExpression(expression, snapshot.Length), snapshot);
    }

    internal static ExpressionSnapshot? CreateSnapshot(int termCount, IReadOnlyList<SearchOperator>? connectors,
        SearchOperator defaultOperator)
    {
        ValidateTermCount(termCount);
        var joins = SnapshotConnectors(termCount, connectors, defaultOperator);
        if (termCount == 0) return null;
        // Construct bounded source nodes, then use the same validator/compiler as explicit trees.
        var groups = new List<SearchExpression>();
        SearchExpression group = new TermExpression(0);
        for (var index = 1; index < termCount; index++)
        {
            var term = new TermExpression(index);
            if ((joins?[index - 1] ?? defaultOperator) == SearchOperator.And)
                group = new BinaryExpression(SearchOperator.And, group, term);
            else
            {
                groups.Add(group);
                group = term;
            }
        }
        groups.Add(group);
        SearchExpression expression = groups[0];
        for (var index = 1; index < groups.Count; index++)
            expression = new BinaryExpression(SearchOperator.Or, expression, groups[index]);
        return SnapshotExpression(expression, termCount);
    }

    private static SearchOperator[]? SnapshotConnectors(int termCount, IReadOnlyList<SearchOperator>? connectors,
        SearchOperator defaultOperator)
    {
        if (!Enum.IsDefined(defaultOperator))
            throw new ArgumentException("Search operator must be AND or OR.", nameof(defaultOperator));
        if (connectors is null) return null;
        if (connectors.Count != Math.Max(0, termCount - 1))
            throw new ArgumentException("Adjacent connector count must equal the original keyword count minus one.", nameof(connectors));
        var snapshot = connectors.ToArray();
        if (snapshot.Length != Math.Max(0, termCount - 1) || snapshot.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("Adjacent connectors must contain exactly one AND or OR between each original keyword.", nameof(connectors));
        return snapshot;
    }

    /// <summary>Bounds each occurrence before descending; never invokes record equality, hashing, or recursion.</summary>
    internal static ExpressionSnapshot SnapshotExpression(SearchExpression expression, int termCount)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ValidateTermCount(termCount);
        var seen = new bool[termCount];
        var nodes = new List<NodeSnapshot>();
        var pending = new Stack<(SearchExpression? Node, int Depth, int Parent, bool Right)>();
        pending.Push((expression, 1, -1, false));
        while (pending.TryPop(out var next))
        {
            if (next.Depth > MaxExpressionDepth)
                throw InvalidExpression($"Expression exceeds MaxExpressionDepth={MaxExpressionDepth}.");
            if (nodes.Count >= MaxExpressionNodes)
                throw InvalidExpression($"Expression exceeds MaxExpressionNodes={MaxExpressionNodes}.");

            NodeSnapshot node;
            SearchExpression? left = null, right = null;
            switch (next.Node)
            {
                case TermExpression term:
                    if ((uint)term.TermIndex >= (uint)termCount)
                        throw InvalidExpression("TermIndex must reference an original keyword position.");
                    if (seen[term.TermIndex])
                        throw InvalidExpression("Each original keyword position must occur exactly once; duplicate TermIndex.");
                    seen[term.TermIndex] = true;
                    node = new(NodeKind.Term, term.TermIndex);
                    break;
                case BinaryExpression binary:
                    if (!Enum.IsDefined(binary.Operator))
                        throw InvalidExpression("Expression operator must be AND or OR.");
                    left = binary.Left;
                    right = binary.Right;
                    if (left is null || right is null) throw InvalidExpression("Expression children must be nonnull.");
                    node = new(binary.Operator == SearchOperator.And ? NodeKind.And : NodeKind.Or);
                    break;
                case GroupExpression group:
                    left = group.Inner;
                    if (left is null) throw InvalidExpression("Expression children must be nonnull.");
                    node = new(NodeKind.Group);
                    break;
                case null:
                    throw InvalidExpression("Expression children must be nonnull.");
                default:
                    throw InvalidExpression("Unknown SearchExpression node type.");
            }

            var index = nodes.Count;
            nodes.Add(node);
            if (next.Parent >= 0)
            {
                var parent = nodes[next.Parent];
                nodes[next.Parent] = next.Right ? parent with { Right = index } : parent with { Left = index };
            }
            // Count actual visits, including shared nodes. Cycles/DAG expansion are bounded too.
            if (right is not null) pending.Push((right, next.Depth + 1, index, true));
            if (left is not null) pending.Push((left, next.Depth + 1, index, false));
        }
        if (seen.Any(value => !value))
            throw InvalidExpression("Each original keyword position must occur exactly once; missing TermIndex.");

        var clones = new SearchExpression[nodes.Count];
        var steps = new List<EvaluationStep>(nodes.Count);
        // Reverse preorder is right/left/postorder. Each child is ready before its parent.
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            var node = nodes[index];
            switch (node.Kind)
            {
                case NodeKind.Term:
                    clones[index] = new TermExpression(node.TermIndex);
                    steps.Add(new(SearchOperator.And, node.TermIndex));
                    break;
                case NodeKind.Group:
                    clones[index] = new GroupExpression(clones[node.Left]);
                    break;
                default:
                    var operation = node.Kind == NodeKind.And ? SearchOperator.And : SearchOperator.Or;
                    clones[index] = new BinaryExpression(operation, clones[node.Left], clones[node.Right]);
                    steps.Add(new(operation, -1));
                    break;
            }
        }
        return new(clones[0], steps.ToArray());
    }

    /// <summary>Runs only prevalidated instructions. Engine inputs map original positions to distinct literals.</summary>
    internal static bool EvaluateValidated(ExpressionSnapshot expression, IReadOnlyList<bool> matches,
        IReadOnlyList<int>? termIndices = null)
    {
        Span<bool> values = stackalloc bool[MaxExpressionNodes];
        var count = 0;
        foreach (var step in expression.Steps)
        {
            if (step.TermIndex >= 0)
                values[count++] = matches[termIndices?[step.TermIndex] ?? step.TermIndex];
            else
            {
                values[count - 2] = step.Operator == SearchOperator.And
                    ? values[count - 2] & values[count - 1]
                    : values[count - 2] | values[count - 1];
                count--;
            }
        }
        return values[0];
    }

    private static string[] SnapshotTerms(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ValidateTermCount(terms.Count);
        var snapshot = terms.ToArray();
        ValidateTermCount(snapshot.Length);
        if (snapshot.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Keywords must be nonempty literal text.", nameof(terms));
        return snapshot;
    }

    private static void ValidateTermCount(int termCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(termCount);
        // A binary tree with N leaves requires at least 2N-1 nodes; bound before allocation.
        if (termCount > (MaxExpressionNodes + 1) / 2)
            throw new ArgumentException($"Keyword positions exceed the MaxExpressionNodes={MaxExpressionNodes} budget.", nameof(termCount));
    }

    private static ArgumentException InvalidExpression(string message) => new(message, "expression");

    private static string FormatValidated(SearchExpression expression, IReadOnlyList<string> terms)
    {
        var output = new StringBuilder();
        AppendExpression(output, expression, terms, null);
        return output.ToString();
    }

    // Recursion is permitted only on the immutable clone, already bounded to depth 128.
    private static void AppendExpression(StringBuilder output, SearchExpression expression, IReadOnlyList<string> terms,
        SearchOperator? parentOperator)
    {
        switch (expression)
        {
            case TermExpression term:
                output.Append(FormatTerm(terms[term.TermIndex]));
                break;
            case GroupExpression group:
                output.Append('(');
                AppendExpression(output, group.Inner, terms, null);
                output.Append(')');
                break;
            case BinaryExpression binary:
                // AND under OR retains the original adjacent-join display; OR under AND requires parentheses.
                var brackets = parentOperator.HasValue && parentOperator.Value != binary.Operator;
                if (brackets) output.Append('(');
                AppendExpression(output, binary.Left, terms, binary.Operator);
                output.Append(binary.Operator == SearchOperator.And ? " AND " : " OR ");
                AppendExpression(output, binary.Right, terms, binary.Operator);
                if (brackets) output.Append(')');
                break;
        }
    }

    private enum NodeKind { Term, And, Or, Group }
    private readonly record struct NodeSnapshot(NodeKind Kind, int TermIndex = -1, int Left = -1, int Right = -1);
    internal readonly record struct EvaluationStep(SearchOperator Operator, int TermIndex);
    internal sealed record ExpressionSnapshot(SearchExpression Expression, EvaluationStep[] Steps);

    private static string FormatTerm(string term)
    {
        if (!term.Contains("AND", StringComparison.OrdinalIgnoreCase) &&
            !term.Contains("OR", StringComparison.OrdinalIgnoreCase) &&
            !term.Any(value => char.IsWhiteSpace(value) || char.IsControl(value) || value is '(' or ')' or '"' or '\\'))
            return term;
        var quoted = new StringBuilder("\"");
        foreach (var value in term)
        {
            quoted.Append(value switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(value) => "\\u" + ((int)value).ToString("X4"),
                _ => value.ToString()
            });
        }
        return quoted.Append('"').ToString();
    }
}
