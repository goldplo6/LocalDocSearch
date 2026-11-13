using System.Globalization;
using System.Runtime.Versioning;

namespace LocalDocSearch.Navigation;

[SupportedOSPlatform("windows")]
internal static partial class OfficeComNavigation
{
    private static OfficeLocationEvidence LocateWord(dynamic application, dynamic document, object? protectedWindow,
        OfficeTarget target, ComScope scope, string note, CancellationToken token)
    {
        var path = (string)document.FullName;
        var evidence = new OfficeLocationEvidence(true, note, path);
        if (target.TextTarget is not { } textTarget) return evidence;
        token.ThrowIfCancellationRequested();
        dynamic revisions = scope.Take((object)document.Revisions);
        if (Convert.ToInt32(revisions.Count, CultureInfo.InvariantCulture) != 0)
            return evidence with { Message = note + "当前文档含修订；提取段落与原生范围尚未可靠对应。" };
        if (protectedWindow is not null)
        {
            dynamic protectedView = protectedWindow;
            protectedView.Activate();
        }
        else document.Activate();
        token.ThrowIfCancellationRequested();
        dynamic content = scope.Take((object)document.Content);
        dynamic paragraphs = scope.Take((object)content.Paragraphs);
        var count = Convert.ToInt32(paragraphs.Count, CultureInfo.InvariantCulture);
        var bodyParagraph = 0;
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            dynamic paragraph = scope.Take((object)paragraphs.Item[index]);
            dynamic paragraphRange = scope.Take((object)paragraph.Range);
            // The extractor numbers body paragraphs separately from every table cell.
            if (Convert.ToBoolean(paragraphRange.Information[12], CultureInfo.InvariantCulture)) continue; // wdWithInTable
            if (++bodyParagraph != textTarget.Match.Location.Paragraph) continue;
            dynamic fields = scope.Take((object)paragraphRange.Fields);
            if (Convert.ToInt32(fields.Count, CultureInfo.InvariantCulture) != 0)
                return evidence with { Message = note + "目标段落含字段；未猜测字段结果与提取文字的偏移。" };
            var raw = (string)paragraphRange.Text;
            var text = OfficeTextNavigation.ParagraphText(raw);
            var paragraphStart = Convert.ToInt32(paragraphRange.Start, CultureInfo.InvariantCulture);
            var paragraphEnd = Convert.ToInt32(paragraphRange.End, CultureInfo.InvariantCulture);
            if (paragraphEnd - paragraphStart != raw.Length || !OfficeTextNavigation.MatchesParagraph(textTarget, text))
                return evidence with { Message = note + "来源段落、偏移或唯一上下文未通过核验；未选择首次同词。" };
            var match = textTarget.Match;
            var start = checked(paragraphStart + match.Offset);
            // Range.Find is bounded to the expected offset. A short literal anchor avoids Word's
            // FindText length ceiling; the complete original match is verified before selection.
            var probe = OfficeTextNavigation.WordFindProbe(match.Text);
            if (probe is null) return evidence with { Message = note + "命中文字缺少可可靠查找的字面量前缀。" };
            token.ThrowIfCancellationRequested();
            var matchEnd = checked(start + match.Text.Length);
            dynamic foundRange = scope.Take((object)document.Range(start,
                OfficeTextNavigation.WordFindRangeEnd(matchEnd, paragraphEnd)));
            dynamic find = scope.Take((object)foundRange.Find);
            find.ClearFormatting();
            var found = Convert.ToBoolean(find.Execute(FindText: probe, MatchCase: true, MatchWholeWord: false,
                MatchWildcards: false, MatchSoundsLike: false, MatchAllWordForms: false,
                Forward: true, Wrap: 0, Format: false, Replace: 0), CultureInfo.InvariantCulture);
            token.ThrowIfCancellationRequested();
            if (!found || Convert.ToInt32(foundRange.Start, CultureInfo.InvariantCulture) != start)
                return evidence with { Message = note + "Word Find 未在来源偏移返回字面量前缀。" };
            dynamic range = scope.Take((object)document.Range(start, matchEnd));
            if (!string.Equals((string)range.Text, match.Text, StringComparison.Ordinal))
                return evidence with { Message = note + "Word 完整命中文字读回不一致。" };
            token.ThrowIfCancellationRequested();
            range.Select();
            token.ThrowIfCancellationRequested();
            dynamic selection = scope.Take((object)application.Selection);
            dynamic selectedRange = scope.Take((object)selection.Range);
            dynamic selectedDocument = scope.Take((object)selectedRange.Document);
            var selectedPath = (string)selectedDocument.FullName;
            var actualStart = Convert.ToInt32(selectedRange.Start, CultureInfo.InvariantCulture);
            var actualEnd = Convert.ToInt32(selectedRange.End, CultureInfo.InvariantCulture);
            if (Convert.ToInt32(selectedRange.StoryType, CultureInfo.InvariantCulture) != 1 ||
                actualStart < paragraphStart || actualEnd > paragraphEnd)
                return evidence with { Message = note + "实际选区已离开目标正文段落。" };
            // Re-read the source paragraph after selection; never manufacture evidence from the request text.
            raw = (string)paragraphRange.Text;
            token.ThrowIfCancellationRequested();
            return new OfficeLocationEvidence(true, note, selectedPath)
            {
                TextSelection = new OfficeTextSelectionEvidence(OfficeTextNavigation.ParagraphText(raw),
                    (string)selectedRange.Text, Convert.ToInt32(paragraphRange.Start, CultureInfo.InvariantCulture),
                    actualStart, actualEnd - actualStart, bodyParagraph)
            };
        }
        return evidence with { Message = note + "当前正文不存在所请求的来源段落。" };
    }

    private static OfficeLocationEvidence SelectSlideText(dynamic window, dynamic slide, OfficeTarget target,
        ComScope scope, OfficeLocationEvidence evidence, CancellationToken token)
    {
        var textTarget = target.TextTarget!;
        var match = textTarget.Match;
        var objectIndex = textTarget.ObjectIndex!.Value;
        var paragraphNumber = match.Location.Paragraph!.Value;
        token.ThrowIfCancellationRequested();
        dynamic shapes = scope.Take((object)slide.Shapes);
        if (objectIndex > Convert.ToInt32(shapes.Count, CultureInfo.InvariantCulture))
            return evidence with { Message = evidence.Message + "来源顶层对象序号已超出当前幻灯片。" };
        dynamic shape = scope.Take((object)shapes.Item[objectIndex]);
        if (!string.Equals((string)shape.Name, match.Location.ObjectName, StringComparison.Ordinal) ||
            Convert.ToInt32(shape.HasTextFrame, CultureInfo.InvariantCulture) != -1)
            return evidence with { Message = evidence.Message + "来源对象名称或文本类型未通过核验。" };
        dynamic frame = scope.Take((object)shape.TextFrame);
        dynamic allText = scope.Take((object)frame.TextRange);
        dynamic allParagraphs = scope.Take((object)allText.Paragraphs());
        // PowerPoint silently clamps out-of-range Paragraphs/Characters indices to the last item.
        if (paragraphNumber > Convert.ToInt32(allParagraphs.Count, CultureInfo.InvariantCulture))
            return evidence with { Message = evidence.Message + "来源段落序号已超出当前文本框。" };
        dynamic paragraph = scope.Take((object)allText.Paragraphs(paragraphNumber, 1));
        var raw = (string)paragraph.Text;
        if (Convert.ToInt32(paragraph.Length, CultureInfo.InvariantCulture) != raw.Length ||
            !OfficeTextNavigation.MatchesParagraph(textTarget, OfficeTextNavigation.ParagraphText(raw)))
            return evidence with { Message = evidence.Message + "来源偏移或唯一上下文未通过核验；未选择首次同词。" };
        token.ThrowIfCancellationRequested();
        dynamic characters = scope.Take((object)paragraph.Characters(match.Offset + 1, match.Text.Length));
        if (!string.Equals((string)characters.Text, match.Text, StringComparison.Ordinal)) return evidence;
        token.ThrowIfCancellationRequested();
        characters.Select();
        token.ThrowIfCancellationRequested();
        dynamic selection = scope.Take((object)window.Selection);
        if (Convert.ToInt32(selection.Type, CultureInfo.InvariantCulture) != 3) return evidence; // ppSelectionText
        dynamic selectedText = scope.Take((object)selection.TextRange);
        dynamic selectedShapes = scope.Take((object)selection.ShapeRange);
        if (Convert.ToInt32(selectedShapes.Count, CultureInfo.InvariantCulture) != 1) return evidence;
        dynamic selectedShape = scope.Take((object)selectedShapes.Item[1]);
        dynamic selectedSlide = scope.Take((object)selectedShape.Parent);
        var selectedSlideIndex = Convert.ToInt32(selectedSlide.SlideIndex, CultureInfo.InvariantCulture);
        if (selectedSlideIndex != target.Slide) return evidence;
        var selectedId = Convert.ToInt32(selectedShape.Id, CultureInfo.InvariantCulture);
        // Derive the actual selected object's ordinal from its read-back ID, not from the requested index.
        var actualObjectIndex = 0;
        for (var index = 1; index <= Convert.ToInt32(shapes.Count, CultureInfo.InvariantCulture); index++)
        {
            token.ThrowIfCancellationRequested();
            dynamic candidate = scope.Take((object)shapes.Item[index]);
            if (Convert.ToInt32(candidate.Id, CultureInfo.InvariantCulture) == selectedId)
            { actualObjectIndex = index; break; }
        }
        dynamic selectedFrame = scope.Take((object)selectedShape.TextFrame);
        dynamic selectedAllText = scope.Take((object)selectedFrame.TextRange);
        dynamic selectedParagraphs = scope.Take((object)selectedAllText.Paragraphs());
        if (paragraphNumber > Convert.ToInt32(selectedParagraphs.Count, CultureInfo.InvariantCulture)) return evidence;
        dynamic selectedParagraph = scope.Take((object)selectedAllText.Paragraphs(paragraphNumber, 1));
        dynamic currentView = scope.Take((object)window.View);
        dynamic currentSlide = scope.Take((object)currentView.Slide);
        dynamic currentPresentation = scope.Take((object)window.Presentation);
        token.ThrowIfCancellationRequested();
        return new OfficeLocationEvidence(true, evidence.Message, (string)currentPresentation.FullName,
            Slide: Convert.ToInt32(currentSlide.SlideIndex, CultureInfo.InvariantCulture))
        {
            TextSelection = new OfficeTextSelectionEvidence(OfficeTextNavigation.ParagraphText((string)selectedParagraph.Text),
                (string)selectedText.Text, Convert.ToInt32(selectedParagraph.Start, CultureInfo.InvariantCulture),
                Convert.ToInt32(selectedText.Start, CultureInfo.InvariantCulture),
                Convert.ToInt32(selectedText.Length, CultureInfo.InvariantCulture), paragraphNumber,
                actualObjectIndex, (string)selectedShape.Name)
        };
    }
}
