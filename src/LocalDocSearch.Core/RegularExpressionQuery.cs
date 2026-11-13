using System.Text.RegularExpressions;

namespace LocalDocSearch.Core;

/// <summary>Shared validation for the independent .NET regular-expression search mode.</summary>
public static class RegularExpressionQuery
{
    /// <summary>Returns null for a valid pattern, otherwise a Chinese validation message.
    /// Syntax error positions are zero-based UTF-16 offsets from RegexParseException.Offset.
    /// Resource-limit configuration is validated in the same way as TextSearchEngine.</summary>
    public static string? Validate(string? pattern, bool matchCase = false, SearchResourceLimits? limits = null) =>
        ValidateAndCreate(pattern, matchCase, (limits ?? new()).Snapshot(), out _);

    // The engine keeps this one instance for the entire query, rather than validating and
    // constructing twice or using the process-wide static Regex cache.
    internal static string? ValidateAndCreate(string? pattern, bool matchCase, SearchResourceLimits limits,
        out Regex? regularExpression)
    {
        regularExpression = null;
        if (string.IsNullOrEmpty(pattern)) return "请输入非空正则表达式。";
        if (pattern.Length > limits.MaxRegexPatternCharacters)
            return $"正则表达式的 UTF-16 长度须不超过 {limits.MaxRegexPatternCharacters}（MaxRegexPatternCharacters）。";
        var options = RegexOptions.CultureInvariant;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        try
        {
            // Keep standard .NET syntax, including lookarounds, backreferences and inline options.
            // The timeout is always explicit and finite; user patterns cannot alter it.
            regularExpression = new Regex(pattern, options, TimeSpan.FromMilliseconds(limits.RegexMatchTimeoutMilliseconds));
            return null;
        }
        catch (RegexParseException exception)
        {
            // Do not echo the pattern from the framework exception's message.
            return $"正则表达式语法错误（字符位置 {exception.Offset}，从 0 开始的 UTF-16 偏移；错误代码 {exception.Error}）。";
        }
        catch (ArgumentException)
        {
            return "正则表达式无效，请检查语法。";
        }
    }
}
