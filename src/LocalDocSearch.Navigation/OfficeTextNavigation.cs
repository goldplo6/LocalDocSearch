using System.Globalization;
using LocalDocSearch.Core;

namespace LocalDocSearch.Navigation;

/// <summary>Extraction identities and literal UTF-16 offsets, never a first-word heuristic.</summary>
internal static class OfficeTextNavigation
{
    internal static bool TryCreate(MatchItem match, out OfficeTextTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(match.Text) || match.Text.Length > 32768 || match.Offset < 0 ||
            match.Before is null || match.After is null || match.Before.Length > match.Offset ||
            match.Before.Length > 32768 || match.After.Length > 32768 ||
            match.Location.Paragraph is not > 0) return false;
        var source = match.Location;
        if (source.Kind == SourceKind.WordParagraph && source.Table is null && source.Row is null && source.Cell is null &&
            match.SourceSegmentId == FormattableString.Invariant($"word:p{source.Paragraph}"))
        {
            target = new OfficeTextTarget(match);
            return true;
        }
        if (source.Kind != SourceKind.SlideText || source.Slide is not > 0 || source.Row is not null ||
            source.Cell is not null || string.IsNullOrEmpty(source.ObjectName) || match.SourceSegmentId is not { } id)
            return false;
        var prefix = FormattableString.Invariant($"ppt:s{source.Slide}:o");
        var suffix = FormattableString.Invariant($":r:c:p{source.Paragraph}");
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || !id.EndsWith(suffix, StringComparison.Ordinal) ||
            id.Length <= prefix.Length + suffix.Length) return false;
        var number = id.AsSpan(prefix.Length, id.Length - prefix.Length - suffix.Length);
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var objectIndex) || objectIndex < 1 ||
            !number.SequenceEqual(objectIndex.ToString(CultureInfo.InvariantCulture))) return false;
        target = new OfficeTextTarget(match, objectIndex);
        return true;
    }

    // Both Office paragraph terminators and soft line breaks occupy one native position.
    // Leave all other whitespace/case alone: normalization must never hide a shifted match.
    internal static string ParagraphText(string raw) =>
        (raw.EndsWith('\r') ? raw[..^1] : raw).Replace('\v', '\n');

    internal static bool MatchesParagraph(OfficeTextTarget target, string paragraphText)
    {
        var match = target.Match;
        if (paragraphText.Length > 1_000_000 || paragraphText.Contains('\r') ||
            match.Offset > paragraphText.Length - match.Text.Length || match.Before.Length > match.Offset)
            return false;
        var context = match.Before + match.Text + match.After;
        var start = match.Offset - match.Before.Length;
        if (context.Length > paragraphText.Length - start ||
            !paragraphText.AsSpan(start, context.Length).SequenceEqual(context)) return false;
        // Even at the stored offset, indistinguishable repeated context could represent a stale occurrence.
        return paragraphText.IndexOf(context, StringComparison.Ordinal) == start &&
            paragraphText.IndexOf(context, start + 1, StringComparison.Ordinal) < 0;
    }

    // Word can skip an exact-sized Find range and continue beyond its end. Include one
    // following character when available, while staying within the verified paragraph.
    internal static int WordFindRangeEnd(int matchEnd, int paragraphEnd) =>
        matchEnd < paragraphEnd ? matchEnd + 1 : matchEnd;

    internal static string? WordFindProbe(string text)
    {
        var length = Math.Min(100, text.Length);
        for (var index = 0; index < length; index++)
        {
            if (char.IsControl(text[index])) { length = index; break; }
        }
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return length == 0 ? null : text[..length].Replace("^", "^^", StringComparison.Ordinal);
    }

    internal static bool MatchesSelection(OfficeTextTarget target, OfficeTextSelectionEvidence selection) =>
        selection.Paragraph == target.Match.Location.Paragraph &&
        selection.ObjectIndex == target.ObjectIndex &&
        (target.ObjectIndex is null || string.Equals(selection.ObjectName, target.Match.Location.ObjectName, StringComparison.Ordinal)) &&
        selection.ParagraphStart >= 0 && selection.SelectionStart >= 0 &&
        (long)selection.SelectionStart == (long)selection.ParagraphStart + target.Match.Offset &&
        selection.SelectionLength == target.Match.Text.Length &&
        string.Equals(selection.SelectedText, target.Match.Text, StringComparison.Ordinal) &&
        MatchesParagraph(target, selection.ParagraphText);
}
