using System.Text;

namespace LocalDocSearch.Core;

/// <summary>A parsed formula, or an error at a zero-based UTF-16 offset (including end of input).</summary>
public sealed record LogicalQueryParseResult(bool Success, IReadOnlyList<string> Terms,
    SearchExpression? Expression, string Formula, string Error, int ErrorOffset);

/// <summary>Parses explicit AND/OR formulas without I/O or unbounded recursion.</summary>
public static class LogicalQueryParser
{
    public const int MaximumFormulaCharacters = 262144;

    /// <summary>AND binds before OR. Every explicit pair of parentheses becomes a GroupExpression.
    /// Invalid user text returns a failed result. Invalid resource limits throw ArgumentOutOfRangeException,
    /// using the same validation as search requests, including resource limits unrelated to parsing.</summary>
    public static LogicalQueryParseResult Parse(string? text, SearchResourceLimits? limits = null)
    {
        var snapshot = (limits ?? new SearchResourceLimits()).Snapshot();
        text ??= string.Empty;
        // Check before tokenization, builders, stacks, or copies of the source text.
        if (text.Length > MaximumFormulaCharacters)
            return Failure(MaximumFormulaCharacters,
                $"公式长度超过 MaximumFormulaCharacters={MaximumFormulaCharacters} 个 UTF-16 字符。");
        return new Parser(text, snapshot).Run();
    }

    private static LogicalQueryParseResult Failure(int offset, string message) =>
        new(false, Array.Empty<string>(), null, string.Empty,
            $"位置 {offset}（UTF-16，从 0 开始）：{message}", offset);

    private sealed class Parser(string text, SearchResourceLimits limits)
    {
        private readonly List<string> _terms = [];
        private readonly Stack<ParsedExpression> _values = new();
        private readonly Stack<PendingOperation> _operations = new();
        private LogicalQueryParseResult? _failure;
        private int _position;
        private int _openGroups;
        private int _nodeCount;
        private bool _expectOperand = true;

        public LogicalQueryParseResult Run()
        {
            while (_position < text.Length)
            {
                var character = text[_position];
                if (char.IsWhiteSpace(character))
                {
                    _position++;
                    continue;
                }
                var offset = _position;
                if (character == '(')
                {
                    if (!_expectOperand) return Error(offset, "左括号前缺少 AND 或 OR 运算符。");
                    // At least one leaf must fit below every pending group.
                    if (_openGroups >= SearchFormula.MaxExpressionDepth - 1)
                        return Error(offset, $"括号嵌套超过 MaxExpressionDepth={SearchFormula.MaxExpressionDepth}。");
                    if (!ReserveNode(offset)) return _failure!;
                    _operations.Push(new(null, offset));
                    _openGroups++;
                    _position++;
                    continue;
                }
                if (character == ')')
                {
                    if (_openGroups == 0) return Error(offset, "右括号没有匹配的左括号。");
                    if (_expectOperand) return Error(offset, "右括号前缺少关键词，括号内须有完整表达式。");
                    while (_operations.Peek().Operator.HasValue)
                        if (!Reduce()) return _failure!;
                    var opening = _operations.Pop();
                    var inner = _values.Pop();
                    if (!CheckDepth(inner.Depth + 1, opening.Offset)) return _failure!;
                    _values.Push(new(new GroupExpression(inner.Expression), inner.Depth + 1));
                    _openGroups--;
                    _position++;
                    continue;
                }
                if (character == '"')
                {
                    if (!_expectOperand) return Error(offset, "关键词之间缺少 AND 或 OR 运算符。");
                    if (!CheckTermCount(offset) || !ReadQuotedTerm(out var literal)) return _failure!;
                    if (!AddTerm(literal, offset)) return _failure!;
                    continue;
                }
                if (char.IsControl(character))
                    return Error(offset, "出现不可见控制字符；请在双引号内使用转义表示。");

                // Bare keywords end only at whitespace, quotes, or parentheses. Do not allocate a token
                // until it has passed the length budget and operator recognition.
                while (_position < text.Length && !char.IsWhiteSpace(text[_position]) &&
                       text[_position] is not ('(' or ')' or '"'))
                {
                    if (char.IsControl(text[_position]))
                        return Error(_position, "关键词包含不可见控制字符；请在双引号内使用转义表示。");
                    _position++;
                }
                var token = text.AsSpan(offset, _position - offset);
                SearchOperator? operation = token.Equals("AND", StringComparison.OrdinalIgnoreCase) ? SearchOperator.And
                    : token.Equals("OR", StringComparison.OrdinalIgnoreCase) ? SearchOperator.Or : null;
                if (operation.HasValue)
                {
                    if (_expectOperand)
                        return Error(offset, "运算符前缺少关键词；文字 AND 或 OR 须用双引号包围。");
                    if (!ReserveNode(offset)) return _failure!;
                    while (_operations.TryPeek(out var previous) && previous.Operator.HasValue &&
                           Precedence(previous.Operator.Value) >= Precedence(operation.Value))
                        if (!Reduce()) return _failure!;
                    _operations.Push(new(operation, offset));
                    _expectOperand = true;
                    continue;
                }
                if (!_expectOperand) return Error(offset, "关键词之间缺少 AND 或 OR 运算符。");
                if (!CheckTermCount(offset)) return _failure!;
                if (token.Length > limits.MaxTermCharacters)
                    return Error(offset + limits.MaxTermCharacters,
                        $"关键词长度超过 MaxTermCharacters={limits.MaxTermCharacters} 个 UTF-16 字符。");
                if (!AddTerm(token.ToString(), offset)) return _failure!;
            }

            if (_expectOperand)
                return Error(text.Length, _terms.Count == 0 && _openGroups == 0
                    ? "请输入非空的逻辑搜索公式。" : "公式末尾缺少关键词或完整表达式。");
            if (_openGroups != 0) return Error(text.Length, "缺少匹配的右括号。");
            while (_operations.Count != 0)
                if (!Reduce()) return _failure!;

            var expression = _values.Pop().Expression;
            var terms = Array.AsReadOnly(_terms.ToArray());
            // Reuse the single authoritative tree validator and formatter. All budgets have already
            // been checked iteratively, so its bounded formatting recursion sees only valid trees.
            var formula = SearchFormula.Format(expression, terms);
            return new(true, terms, expression, formula, string.Empty, -1);
        }

        private bool ReadQuotedTerm(out string literal)
        {
            literal = string.Empty;
            var opening = _position++;
            var builder = new StringBuilder(Math.Min(32, limits.MaxTermCharacters));
            while (_position < text.Length)
            {
                var offset = _position;
                var character = text[_position++];
                if (character == '"')
                {
                    literal = builder.ToString();
                    if (string.IsNullOrWhiteSpace(literal))
                        return Fail(opening, "双引号内的关键词须包含非空白文字。");
                    return true;
                }
                if (char.IsControl(character))
                    return Fail(offset, "双引号内的控制字符须用 \\n、\\r、\\t 或 \\uXXXX 转义。");
                if (character == '\\')
                {
                    if (_position == text.Length) return Fail(offset, "转义未完成，且缺少结束双引号。");
                    var escapeOffset = _position;
                    var escape = text[_position++];
                    switch (escape)
                    {
                        case '"': character = '"'; break;
                        case '\\': character = '\\'; break;
                        case 'n': character = '\n'; break;
                        case 'r': character = '\r'; break;
                        case 't': character = '\t'; break;
                        case 'u':
                            var codeUnit = 0;
                            for (var digit = 0; digit < 4; digit++)
                            {
                                if (_position == text.Length)
                                    return Fail(_position, "Unicode 转义未完成，\\u 后须有四位十六进制数字。");
                                var hex = HexValue(text[_position]);
                                if (hex < 0)
                                    return Fail(_position, "Unicode 转义无效，\\u 后须有四位十六进制数字。");
                                codeUnit = (codeUnit << 4) | hex;
                                _position++;
                            }
                            // Decode one UTF-16 code unit, preserving paired and unpaired surrogates.
                            character = (char)codeUnit;
                            break;
                        default:
                            return Fail(escapeOffset, "转义无效，仅支持 \\\"、\\\\、\\n、\\r、\\t、\\uXXXX。");
                    }
                }
                if (builder.Length >= limits.MaxTermCharacters)
                    return Fail(offset, $"关键词长度超过 MaxTermCharacters={limits.MaxTermCharacters} 个 UTF-16 字符。");
                builder.Append(character);
            }
            return Fail(text.Length, "缺少结束双引号。");
        }

        private bool CheckTermCount(int offset) => _terms.Count < limits.MaxTerms ||
            Fail(offset, $"关键词个数超过 MaxTerms={limits.MaxTerms}（重复关键词也独立计数）。");

        private bool AddTerm(string literal, int offset)
        {
            if (!ReserveNode(offset)) return false;
            _values.Push(new(new TermExpression(_terms.Count), 1));
            _terms.Add(literal);
            _expectOperand = false;
            return true;
        }

        private bool ReserveNode(int offset)
        {
            if (_nodeCount >= SearchFormula.MaxExpressionNodes)
                return Fail(offset, $"公式节点个数超过 MaxExpressionNodes={SearchFormula.MaxExpressionNodes}（含括号）。");
            _nodeCount++;
            return true;
        }

        private bool Reduce()
        {
            var operation = _operations.Pop();
            var right = _values.Pop();
            var left = _values.Pop();
            var depth = Math.Max(left.Depth, right.Depth) + 1;
            if (!CheckDepth(depth, operation.Offset)) return false;
            _values.Push(new(new BinaryExpression(operation.Operator!.Value, left.Expression, right.Expression), depth));
            return true;
        }

        private bool CheckDepth(int depth, int offset) => depth <= SearchFormula.MaxExpressionDepth ||
            Fail(offset, $"公式深度超过 MaxExpressionDepth={SearchFormula.MaxExpressionDepth}（含括号）。");

        private bool Fail(int offset, string message)
        {
            _failure = Error(offset, message);
            return false;
        }

        private static LogicalQueryParseResult Error(int offset, string message) => Failure(offset, message);
        private static int Precedence(SearchOperator operation) => operation == SearchOperator.And ? 2 : 1;
        private static int HexValue(char character) => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'A' and <= 'F' => character - 'A' + 10,
            >= 'a' and <= 'f' => character - 'a' + 10,
            _ => -1
        };

        private readonly record struct ParsedExpression(SearchExpression Expression, int Depth);
        private readonly record struct PendingOperation(SearchOperator? Operator, int Offset);
    }
}
