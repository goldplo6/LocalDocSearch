namespace LocalDocSearch.Core;

/// <summary>A literal keyword expression. Leaves refer to positions in the original Terms list.</summary>
public abstract record SearchExpression;

public sealed record TermExpression(int TermIndex) : SearchExpression;

public sealed record BinaryExpression(SearchOperator Operator, SearchExpression Left, SearchExpression Right) : SearchExpression;

/// <summary>An explicit visual group. Formatting always preserves its parentheses.</summary>
public sealed record GroupExpression(SearchExpression Inner) : SearchExpression;
