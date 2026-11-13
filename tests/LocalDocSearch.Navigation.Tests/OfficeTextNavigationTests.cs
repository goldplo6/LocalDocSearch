using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Navigation.Tests;

public sealed class OfficeTextNavigationTests
{
    private static MatchItem Word(string text = "命中", string before = "前文", string after = "后文", int offset = 2) =>
        new(text, before, text, after, new SourceLocator(SourceKind.WordParagraph, Paragraph: 2), "word:p2", offset);

    private static MatchItem Slide() => new("命中", "前文", "命中", "后文",
        new SourceLocator(SourceKind.SlideText, Paragraph: 2, Slide: 3, ObjectName: "文本框 7"),
        "ppt:s3:o7:r:c:p2", 2);

    [Fact]
    public void Canonical_word_source_retains_offset_context_and_identity()
    {
        var match = Word();
        Assert.True(OfficeTextNavigation.TryCreate(match, out var target));
        Assert.Same(match, target!.Match);
        Assert.Null(target.ObjectIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("word:p1")]
    [InlineData("word:p02")]
    [InlineData("word:t1:r1:c1:p2")]
    public void Missing_or_inconsistent_word_identity_is_not_an_automation_target(string? id) =>
        Assert.False(OfficeTextNavigation.TryCreate(Word() with { SourceSegmentId = id }, out _));

    [Fact]
    public void Table_source_and_body_source_are_never_interchanged()
    {
        var match = Word();
        Assert.False(OfficeTextNavigation.TryCreate(match with
        {
            Location = match.Location with { Table = 1, Row = 1, Cell = 1 }
        }, out _));
        Assert.False(OfficeTextNavigation.TryCreate(match with
        {
            Location = match.Location with { Kind = SourceKind.WordTableCell }
        }, out _));
    }

    [Fact]
    public void Canonical_ppt_identity_carries_the_extracted_top_level_object_ordinal()
    {
        Assert.True(OfficeTextNavigation.TryCreate(Slide(), out var target));
        Assert.Equal(7, target!.ObjectIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ppt:s2:o7:r:c:p2")]
    [InlineData("ppt:s3:o7:r:c:p1")]
    [InlineData("ppt:s3:o07:r:c:p2")]
    [InlineData("ppt:s3:o0:r:c:p2")]
    [InlineData("ppt:s3:o2147483648:r:c:p2")]
    [InlineData("ppt:s3:o7:r1:c1:p2")]
    [InlineData("ppt:s3:o+7:r:c:p2")]
    public void Ppt_source_ids_are_structural_not_first_named_shape_hints(string? id) =>
        Assert.False(OfficeTextNavigation.TryCreate(Slide() with { SourceSegmentId = id }, out _));

    [Fact]
    public void Ppt_missing_name_and_table_cell_are_slide_only()
    {
        var match = Slide();
        Assert.False(OfficeTextNavigation.TryCreate(match with { Location = match.Location with { ObjectName = null } }, out _));
        Assert.False(OfficeTextNavigation.TryCreate(match with
        {
            Location = match.Location with { Kind = SourceKind.SlideTableCell, Row = 1, Cell = 1 }
        }, out _));
    }

    [Fact]
    public void Repeated_word_is_disambiguated_by_exact_source_offset_and_unique_context()
    {
        var match = Word(before: "甲", after: "乙", offset: 4);
        Assert.True(OfficeTextNavigation.TryCreate(match, out var target));
        Assert.True(OfficeTextNavigation.MatchesParagraph(target!, "命中，甲命中乙"));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, "甲命中乙，命中"));
    }

    [Fact]
    public void Repeated_indistinguishable_context_is_not_trusted_even_at_the_stored_offset()
    {
        Assert.True(OfficeTextNavigation.TryCreate(Word(before: "甲", after: "乙", offset: 6), out var target));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, "甲命中乙，甲命中乙"));
    }

    [Fact]
    public void Context_free_unique_match_is_supported_but_repeated_match_is_not()
    {
        Assert.True(OfficeTextNavigation.TryCreate(Word(before: "", after: "", offset: 0), out var target));
        Assert.True(OfficeTextNavigation.MatchesParagraph(target!, "命中"));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, "命中 命中"));
    }

    [Theory]
    [InlineData("前文命中后文字")]
    [InlineData("前文命中后文")]
    public void Literal_full_context_matches_without_requiring_a_paragraph_end(string paragraph)
    {
        Assert.True(OfficeTextNavigation.TryCreate(Word(), out var target));
        Assert.True(OfficeTextNavigation.MatchesParagraph(target!, paragraph));
    }

    [Theory]
    [InlineData("前 文命中后文")]
    [InlineData("前文命中后问")]
    [InlineData("前文命中")]
    [InlineData("")]
    [InlineData("前文\r命中后文")]
    public void Drifted_whitespace_context_offset_or_structure_is_not_fuzzily_repaired(string paragraph)
    {
        Assert.True(OfficeTextNavigation.TryCreate(Word(), out var target));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, paragraph));
    }

    [Fact]
    public void Long_match_requires_full_text_and_context_not_only_the_find_anchor()
    {
        var text = new string('长', 600);
        Assert.True(OfficeTextNavigation.TryCreate(Word(text), out var target));
        Assert.True(OfficeTextNavigation.MatchesParagraph(target!, "前文" + text + "后文"));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, "前文" + text[..599] + "错后文"));
        Assert.Equal(100, OfficeTextNavigation.WordFindProbe(text)!.Length);
    }

    [Theory]
    [InlineData(2, 8, 3)]
    [InlineData(7, 8, 8)]
    [InlineData(8, 8, 8)]
    [InlineData(int.MaxValue - 1, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Word_find_includes_a_following_character_without_leaving_the_verified_paragraph(
        int matchEnd, int paragraphEnd, int expectedEnd)
    {
        Assert.Equal(expectedEnd, OfficeTextNavigation.WordFindRangeEnd(matchEnd, paragraphEnd));
    }

    [Fact]
    public void Word_find_literal_probe_escapes_caret_stops_before_controls_and_keeps_surrogate_pairs()
    {
        Assert.Equal("^^p", OfficeTextNavigation.WordFindProbe("^p"));
        Assert.Equal("前缀", OfficeTextNavigation.WordFindProbe("前缀\n下一行"));
        Assert.Null(OfficeTextNavigation.WordFindProbe("\n下一行"));
        var probe = OfficeTextNavigation.WordFindProbe(new string('x', 99) + "😀尾");
        Assert.Equal(new string('x', 99), probe);
    }

    [Fact]
    public void Paragraph_normalization_removes_only_one_terminator_and_maps_soft_breaks_without_trimming()
    {
        Assert.Equal(" 前\t文\n后 ", OfficeTextNavigation.ParagraphText(" 前\t文\v后 \r"));
        Assert.Equal("文本\r", OfficeTextNavigation.ParagraphText("文本\r\r"));
    }

    [Fact]
    public void Invalid_offsets_empty_text_and_oversize_text_do_not_become_targets()
    {
        Assert.False(OfficeTextNavigation.TryCreate(Word(offset: -1), out _));
        Assert.False(OfficeTextNavigation.TryCreate(Word(offset: 1), out _));
        Assert.False(OfficeTextNavigation.TryCreate(Word(text: ""), out _));
        Assert.False(OfficeTextNavigation.TryCreate(Word(text: new string('x', 32769)), out _));
        Assert.True(OfficeTextNavigation.TryCreate(Word(offset: int.MaxValue), out var target));
        Assert.False(OfficeTextNavigation.MatchesParagraph(target!, "前文命中后文"));
    }

    [Fact]
    public void Readback_must_agree_on_text_coordinates_paragraph_object_and_case()
    {
        Assert.True(OfficeTextNavigation.TryCreate(Slide(), out var target));
        var evidence = new OfficeTextSelectionEvidence("前文命中后文", "命中", 10, 12, 2, 2, 7, "文本框 7");
        Assert.True(OfficeTextNavigation.MatchesSelection(target!, evidence));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { SelectionStart = 13 }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { SelectionLength = 1 }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { SelectedText = "命" }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { Paragraph = 1 }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { ObjectIndex = 1 }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { ObjectName = "文本框 8" }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { ParagraphText = "前文命中后问" }));
        Assert.False(OfficeTextNavigation.MatchesSelection(target!, evidence with { ParagraphStart = int.MaxValue }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verified_native_text_selection_is_exact_only_with_the_office_opt_in(bool word)
    {
        var match = word ? Word() : Slide();
        var backend = new TextBackend(word);
        var request = new LocateRequest(backend.Path, match, true);
        var result = await new WindowsFileNavigator(backend).LocateAsync(request);
        Assert.Equal(LocateLevel.Exact, result.Level);
        Assert.Equal(word ? OfficeTargetKind.WordText : OfficeTargetKind.PowerPointSlide, backend.Target!.Kind);
        Assert.Same(match, backend.Target.TextTarget!.Match);
        Assert.Equal(0, backend.ShellCalls);
        var disabled = new TextBackend(word);
        Assert.Equal(LocateLevel.OpenedOnly, (await new WindowsFileNavigator(disabled).LocateAsync(request with
        { AllowOfficeAutomation = false })).Level);
        Assert.Null(disabled.Target);
        Assert.Equal(1, disabled.ShellCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unverified_text_downgrades_word_to_opened_and_ppt_to_verified_slide(bool word)
    {
        var backend = new TextBackend(word) { WrongSelection = true };
        var result = await new WindowsFileNavigator(backend).LocateAsync(new(backend.Path, word ? Word() : Slide(), true));
        Assert.Equal(word ? LocateLevel.OpenedOnly : LocateLevel.Section, result.Level);
        Assert.Equal(0, backend.ShellCalls);
    }

    [Fact]
    public async Task Text_evidence_cannot_override_wrong_document_or_slide()
    {
        var pathBackend = new TextBackend(true) { WrongPath = true };
        Assert.Equal(LocateLevel.Failed, (await new WindowsFileNavigator(pathBackend)
            .LocateAsync(new(pathBackend.Path, Word(), true))).Level);
        var slideBackend = new TextBackend(false) { WrongSlide = true };
        Assert.Equal(LocateLevel.OpenedOnly, (await new WindowsFileNavigator(slideBackend)
            .LocateAsync(new(slideBackend.Path, Slide(), true))).Level);
    }

    private sealed class TextBackend(bool word) : INavigationBackend
    {
        public bool IsWindows => true;
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), word ? "source.docx" : "source.pptx");
        internal OfficeTarget? Target { get; private set; }
        internal int ShellCalls { get; private set; }
        internal bool WrongSelection { get; init; }
        internal bool WrongPath { get; init; }
        internal bool WrongSlide { get; init; }
        public Task<bool> FileExistsAsync(string fullPath, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task OpenShellAsync(string fullPath, CancellationToken cancellationToken) { ShellCalls++; return Task.CompletedTask; }
        public Task<OfficeLocationEvidence> LocateOfficeAsync(OfficeTarget target, CancellationToken cancellationToken)
        {
            Target = target;
            return Task.FromResult(new OfficeLocationEvidence(true, "read-back", WrongPath ? null : Path,
                Slide: word ? null : WrongSlide ? 4 : 3)
            {
                TextSelection = new OfficeTextSelectionEvidence("前文命中后文", WrongSelection ? "别词" : "命中", 10, 12, 2,
                    2, word ? null : 7, word ? null : "文本框 7")
            });
        }
    }
}
