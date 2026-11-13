using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Core.Tests;

public sealed class LogicalQueryParserTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AllEightTruthCombinationsRespectPrecedenceAndExplicitGroups(int present)
    {
        var matches = Enumerable.Range(0, 3).Select(index => (present & (1 << index)) != 0).ToArray();
        var cases = new (string Text, string Formula, bool Expected)[]
        {
            ("A AND B OR C", "(A AND B) OR C", matches[0] && matches[1] || matches[2]),
            ("A OR B AND C", "A OR (B AND C)", matches[0] || matches[1] && matches[2]),
            ("A AND (B OR C)", "A AND (B OR C)", matches[0] && (matches[1] || matches[2])),
            ("(A OR B) AND C", "(A OR B) AND C", (matches[0] || matches[1]) && matches[2]),
            ("((A) OR (B AND (C)))", "((A) OR (B AND (C)))", matches[0] || matches[1] && matches[2])
        };
        foreach (var (text, formula, expected) in cases)
        {
            var result = AssertSuccess(text);
            Assert.Equal(new[] { "A", "B", "C" }, result.Terms);
            Assert.Equal(formula, result.Formula);
            Assert.Equal(expected, SearchFormula.Evaluate(result.Expression!, matches));
        }
    }

    [Theory]
    [InlineData("a aNd(b oR c)", "a AND (b OR c)")]
    [InlineData(" ((A)) ", "((A))")]
    [InlineData("A\tAND\r\nB", "A AND B")]
    [InlineData("A\u00a0OR\u3000B", "A OR B")]
    [InlineData("A OR B OR C", "A OR B OR C")]
    [InlineData("A AND B AND C", "A AND B AND C")]
    public void NormalizesOperatorCaseAndSpacingWhileKeepingExplicitGroups(string text, string formula)
    {
        Assert.Equal(formula, AssertSuccess(text).Formula);
    }

    [Fact]
    public void AssociativeOperatorsProduceLeftAssociativeTrees()
    {
        var root = Assert.IsType<BinaryExpression>(AssertSuccess("A OR B OR C").Expression);
        Assert.Equal(SearchOperator.Or, root.Operator);
        Assert.Equal(new TermExpression(2), root.Right);
        var left = Assert.IsType<BinaryExpression>(root.Left);
        Assert.Equal(new TermExpression(0), left.Left);
        Assert.Equal(new TermExpression(1), left.Right);
    }

    [Fact]
    public void RepeatedLiteralsAndQuotedOperatorsKeepEveryOriginalPosition()
    {
        var result = AssertSuccess("same OR (same AND \"or\") OR (\"AND\" AND same)");
        Assert.Equal(new[] { "same", "same", "or", "AND", "same" }, result.Terms);
        Assert.Equal("same OR (same AND \"or\") OR (\"AND\" AND same)", result.Formula);
        Assert.True(SearchFormula.Evaluate(result.Expression!, [false, true, true, false, false]));
        Assert.False(SearchFormula.Evaluate(result.Expression!, [false, true, false, true, false]));
        Assert.True(SearchFormula.Evaluate(result.Expression!, [false, false, false, true, true]));
        var terms = Assert.IsAssignableFrom<IList<string>>(result.Terms);
        Assert.True(terms.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => terms[0] = "changed");
    }

    [Theory]
    [InlineData("ANDROID", "ANDROID")]
    [InlineData("orphan", "orphan")]
    [InlineData("XOR", "XOR")]
    [InlineData("path\\file", "path\\file")]
    [InlineData("A+B", "A+B")]
    [InlineData("中文关键词", "中文关键词")]
    [InlineData("\"AND\"", "AND")]
    [InlineData("\"oR\"", "oR")]
    [InlineData("\" A (B) OR C \"", " A (B) OR C ")]
    [InlineData("a\u200bb", "a\u200bb")]
    [InlineData("a\ufeffb", "a\ufeffb")]
    public void OnlyCompleteBareAndOrAreOperatorsAndLiteralTextIsPreserved(string text, string literal)
    {
        var result = AssertSuccess(text);
        Assert.Equal(literal, Assert.Single(result.Terms));
        Assert.Equal(new TermExpression(0), result.Expression);
        Assert.Equal(SearchFormula.Format([literal]), result.Formula);
    }

    [Fact]
    public void QuotedEscapesDecodeExactlyAsFormatterEmitsThem()
    {
        var result = AssertSuccess("\"a\\\"b\\\\c\\n\\r\\t\\u0000\\u001B\\u0061\\uD83D\\uDE42\\uD800\\uDC00\"");
        Assert.Equal("a\"b\\c\n\r\t\0\u001ba\ud83d\ude42\ud800\udc00", Assert.Single(result.Terms));
        var reparsed = AssertSuccess(result.Formula);
        Assert.Equal(result.Terms, reparsed.Terms);
        Assert.Equal(result.Formula, reparsed.Formula);
    }

    [Fact]
    public void FormatterAndParserRoundTripEveryUtf16CodeUnitIncludingUnpairedSurrogates()
    {
        // The prefix keeps even whitespace-only code units valid literal keywords. Batch at the
        // default 64-term limit; this also covers every control escape, quote, slash and surrogate.
        for (var start = 0; start <= char.MaxValue; start += 64)
        {
            var terms = Enumerable.Range(start, 64).Select(value => "x" + (char)value).ToArray();
            var formula = SearchFormula.Format(terms);
            var result = AssertSuccess(formula);
            Assert.Equal(terms, result.Terms);
            Assert.Equal(formula, result.Formula);
        }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData(" \t\n", 3)]
    [InlineData("AND", 0)]
    [InlineData("oR", 0)]
    [InlineData("AND A", 0)]
    [InlineData("A AND", 5)]
    [InlineData("A OR ", 5)]
    [InlineData("A OR AND B", 5)]
    [InlineData("A B", 2)]
    [InlineData("A\"B\"", 1)]
    [InlineData("\"A\"B", 3)]
    [InlineData("A(B)", 1)]
    [InlineData("(A)(B)", 3)]
    [InlineData("(A)!", 3)]
    [InlineData("A XOR B", 2)]
    [InlineData("A && B", 2)]
    [InlineData("A OR B trailing", 7)]
    [InlineData("(", 1)]
    [InlineData("()", 1)]
    [InlineData("( )", 2)]
    [InlineData("(A", 2)]
    [InlineData("(A OR B", 7)]
    [InlineData("A)", 1)]
    [InlineData(")A", 0)]
    [InlineData("A AND )", 6)]
    [InlineData("(A AND)", 6)]
    [InlineData("((A)", 4)]
    [InlineData("\"\"", 0)]
    [InlineData("\" \t\"", 2)]
    [InlineData("\"   \"", 0)]
    [InlineData("\"\\n\\r\\t\\u0020\"", 0)]
    [InlineData("\"A", 2)]
    [InlineData("\"A\\", 2)]
    [InlineData("\"x\\q\"", 3)]
    [InlineData("\"x\\/\"", 3)]
    [InlineData("\"x\\b\"", 3)]
    [InlineData("\"\\U0041\"", 2)]
    [InlineData("\"\\u12", 5)]
    [InlineData("\"\\u12\"", 5)]
    [InlineData("\"\\u12G4\"", 5)]
    [InlineData("\"\\u-001\"", 3)]
    [InlineData("\0", 0)]
    [InlineData("A\0B", 1)]
    [InlineData("\u001b", 0)]
    [InlineData("\"x\u0001\"", 2)]
    [InlineData("\"x\n\"", 2)]
    [InlineData("\"x\t\"", 2)]
    [InlineData("\"x\u007f\"", 2)]
    public void SyntaxErrorsReturnEmptyPayloadAndPreciseChinesePosition(string? text, int offset)
    {
        AssertFailure(text, offset);
    }

    [Fact]
    public void ErrorOffsetsCountUtf16CodeUnitsInsteadOfUnicodeScalars()
    {
        AssertFailure("\U0001f642 B", 3);
        AssertFailure("\"\U0001f642\"B", 4);
        AssertFailure("\U0001f642 AND", 6);
        AssertFailure("\"\U0001f642\\q\"", 4);
        var escaped = AssertSuccess("\"\\uD83D\\uDE42\"");
        Assert.Equal("\U0001f642", Assert.Single(escaped.Terms));
    }

    [Fact]
    public void DefaultTermCountBudgetCountsDuplicateSourcePositions()
    {
        var limits = new SearchResourceLimits();
        Assert.Equal(64, limits.MaxTerms);
        Assert.Equal(4096, limits.MaxTermCharacters);
        var maximum = string.Join(" OR ", Enumerable.Repeat("A", limits.MaxTerms));
        Assert.Equal(limits.MaxTerms, AssertSuccess(maximum).Terms.Count);
        var extra = maximum + " OR A";
        Assert.Contains("MaxTerms=64", AssertFailure(extra, extra.Length - 1).Error);
        var custom = new SearchResourceLimits { MaxTerms = 2 };
        AssertSuccess("A OR A", custom);
        Assert.Contains("MaxTerms=2", AssertFailure("A OR A OR A", 10, custom).Error);
    }

    [Fact]
    public void TermLengthBudgetUsesDecodedUtf16LengthAndChecksBeforeAddingTheNextCharacter()
    {
        var maximum = new string('x', 4096);
        Assert.Equal(maximum, Assert.Single(AssertSuccess(maximum).Terms));
        Assert.Contains("MaxTermCharacters=4096", AssertFailure(maximum + "x", 4096).Error);
        var limits = new SearchResourceLimits { MaxTermCharacters = 2 };
        Assert.Equal("AB", Assert.Single(AssertSuccess("\"\\u0041\\u0042\"", limits).Terms));
        AssertFailure("\"\\u0041\\u0042\\u0043\"", 13, limits);
        Assert.Equal("\U0001f642", Assert.Single(AssertSuccess("\"\\uD83D\\uDE42\"", limits).Terms));
        AssertFailure("\U0001f642x", 2, limits);
        AssertFailure("\"\U0001f642x\"", 3, limits);
    }

    [Fact]
    public void FormulaLengthBudgetIsInclusiveAndWinsBeforeAnyTokenization()
    {
        Assert.Equal(262144, LogicalQueryParser.MaximumFormulaCharacters);
        var maximum = "A" + new string(' ', LogicalQueryParser.MaximumFormulaCharacters - 1);
        Assert.Equal("A", AssertSuccess(maximum).Formula);
        var oversizedMalformed = ")" + new string('(', LogicalQueryParser.MaximumFormulaCharacters);
        var result = AssertFailure(oversizedMalformed, LogicalQueryParser.MaximumFormulaCharacters);
        Assert.Contains("MaximumFormulaCharacters=262144", result.Error);
        Assert.Contains("MaximumFormulaCharacters", AssertFailure(maximum + " ", maximum.Length).Error);
    }

    [Fact]
    public void ExpressionDepthIncludesGroupsAndBinaryNodesWithAnInclusiveLimit()
    {
        var groups = SearchFormula.MaxExpressionDepth - 1;
        var maximum = new string('(', groups) + "A" + new string(')', groups);
        Assert.Equal(maximum, AssertSuccess(maximum).Formula);
        var tooManyGroups = "(" + maximum + ")";
        Assert.Contains("MaxExpressionDepth=128", AssertFailure(tooManyGroups, groups).Error);
        var binaryTooDeep = new string('(', groups) + "A AND B" + new string(')', groups);
        Assert.Contains("MaxExpressionDepth=128", AssertFailure(binaryTooDeep, 0).Error);
        var custom = new SearchResourceLimits { MaxTerms = 128 };
        var longestFlat = string.Join(" AND ", Enumerable.Repeat("A", 128));
        Assert.Equal(longestFlat, AssertSuccess(longestFlat, custom).Formula);
        var wrappedFlat = "(" + longestFlat + ")";
        Assert.Contains("MaxExpressionDepth=128", AssertFailure(wrappedFlat, 0, custom).Error);
    }

    [Fact]
    public void NodeBudgetCountsEachLeafOperatorAndExplicitGroup()
    {
        // 64 leaves + 63 binaries + 128 leaf groups + one outer group = 256 nodes.
        var maximum = "(" + string.Join(" AND ", Enumerable.Repeat("((A))", 64)) + ")";
        Assert.Equal(maximum, AssertSuccess(maximum).Formula);
        var overflow = "(" + maximum + ")";
        var result = LogicalQueryParser.Parse(overflow);
        Assert.False(result.Success);
        Assert.Contains("MaxExpressionNodes=256", result.Error);
        AssertFailure(overflow, result.ErrorOffset);
        // A larger configured MaxTerms must not bypass the fixed expression-node budget.
        var custom = new SearchResourceLimits { MaxTerms = int.MaxValue, MaxTermCharacters = int.MaxValue };
        var manyTerms = string.Join(" OR ", Enumerable.Repeat("A", 129));
        Assert.Contains("MaxExpressionNodes=256", AssertFailure(manyTerms, manyTerms.Length - 1, custom).Error);
    }

    [Fact]
    public void MaliciousNestingAndExtremeConfiguredBudgetsRemainBounded()
    {
        var nested = new string('(', 100_000) + "A" + new string(')', 100_000);
        Assert.Contains("MaxExpressionDepth", AssertFailure(nested, 127).Error);
        var openingsOnly = new string('(', LogicalQueryParser.MaximumFormulaCharacters);
        Assert.Contains("MaxExpressionDepth", AssertFailure(openingsOnly, 127).Error);
        var custom = new SearchResourceLimits { MaxTerms = int.MaxValue, MaxTermCharacters = int.MaxValue };
        var source = "\"" + new string('x', LogicalQueryParser.MaximumFormulaCharacters - 2) + "\"";
        Assert.Equal(source.Length - 2, Assert.Single(AssertSuccess(source, custom).Terms).Length);
    }

    [Fact]
    public void InvalidLimitsThrowTheExistingArgumentOutOfRangeContract()
    {
        var invalid = new (SearchResourceLimits Limits, string Name)[]
        {
            (new() { MaxTerms = 0 }, nameof(SearchResourceLimits.MaxTerms)),
            (new() { MaxTerms = -1 }, nameof(SearchResourceLimits.MaxTerms)),
            (new() { MaxTermCharacters = 0 }, nameof(SearchResourceLimits.MaxTermCharacters)),
            (new() { MaxTermCharacters = -1 }, nameof(SearchResourceLimits.MaxTermCharacters)),
            (new() { MaxSegmentCharacters = 0 }, nameof(SearchResourceLimits.MaxSegmentCharacters)),
            (new() { MaxDirectoryDepth = 0 }, nameof(SearchResourceLimits.MaxDirectoryDepth))
        };
        foreach (var (limits, name) in invalid)
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => LogicalQueryParser.Parse("A", limits));
            Assert.Equal(name, error.ParamName);
        }
    }

    private static LogicalQueryParseResult AssertSuccess(string text, SearchResourceLimits? limits = null)
    {
        var result = LogicalQueryParser.Parse(text, limits);
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Error);
        Assert.Equal(-1, result.ErrorOffset);
        Assert.NotNull(result.Expression);
        Assert.NotEmpty(result.Terms);
        Assert.Equal(SearchFormula.Format(result.Expression!, result.Terms), result.Formula);
        var leaves = new List<int>();
        var pending = new Stack<SearchExpression>();
        pending.Push(result.Expression!);
        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case TermExpression term: leaves.Add(term.TermIndex); break;
                case GroupExpression group: pending.Push(group.Inner); break;
                case BinaryExpression binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;
                default: throw new InvalidOperationException("Unexpected expression node.");
            }
        }
        Assert.Equal(Enumerable.Range(0, result.Terms.Count), leaves);
        return result;
    }

    private static LogicalQueryParseResult AssertFailure(string? text, int offset, SearchResourceLimits? limits = null)
    {
        var result = LogicalQueryParser.Parse(text, limits);
        Assert.False(result.Success);
        Assert.Empty(result.Terms);
        Assert.Null(result.Expression);
        Assert.Empty(result.Formula);
        Assert.Equal(offset, result.ErrorOffset);
        Assert.InRange(result.ErrorOffset, 0, text?.Length ?? 0);
        Assert.Contains($"位置 {offset}（UTF-16，从 0 开始）", result.Error);
        Assert.Matches("[\\u4e00-\\u9fff]", result.Error);
        return result;
    }
}
