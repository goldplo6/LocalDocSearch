using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LocalDocSearch.Core;
using Xunit;

namespace LocalDocSearch.Extractors.Tests;

public sealed class Phase4ReliabilityTests
{
    private static readonly string[] Formats = [".docx", ".xlsx", ".pptx", ".pdf"];
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RelationNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static IEnumerable<object[]> LifecycleCases() => Formats.SelectMany(extension =>
        new[] { "normal", "malformed", "pre-cancelled", "warning-cancelled", "after-first" }
            .Select(mode => new object[] { extension, mode }));

    [Theory]
    [MemberData(nameof(LifecycleCases))]
    public async Task EveryExit_ReleasesOriginalFileAndPreservesItsBytesAndTimestamp(string extension, string mode)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("own-copy" + extension);
        CreateDocument(path, ["apple", "melon"], malformedLate: mode == "malformed");
        var original = Snapshot(path);
        using var cancellation = new CancellationTokenSource();
        if (mode == "pre-cancelled") cancellation.Cancel();
        var context = new ExtractionContext(_ =>
        {
            if (mode == "warning-cancelled") cancellation.Cancel();
        });
        var published = new List<TextSegment>();
        if (mode == "after-first")
        {
            await using var enumerator = Extractor(path).ExtractAsync(path, context, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            // The parser and its stream must already be closed even while enumeration is suspended.
            AssertUnchangedAndReleased(path, original);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        else if (mode == "malformed")
        {
            await Assert.ThrowsAsync<ExtractionException>(() => ReadInto(path, context, cancellation.Token, published));
            Assert.Empty(published);
        }
        else if (mode is "pre-cancelled" or "warning-cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadInto(path, context, cancellation.Token, published));
            Assert.Empty(published);
        }
        else
        {
            await ReadInto(path, context, cancellation.Token, published);
            Assert.Equal(new[] { "apple", "melon" }, published.Select(segment => segment.Text));
        }
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task LockedInput_IsWrappedAndTheSameFileCanBeReadAfterRelease(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("locked" + extension);
        CreateDocument(path, ["apple"]);
        var original = Snapshot(path);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<ExtractionException>(() => ReadInto(path, new ExtractionContext(_ => { })));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        }
        AssertUnchangedAndReleased(path, original);
        var recovered = new List<TextSegment>();
        await ReadInto(path, new ExtractionContext(_ => { }), published: recovered);
        Assert.Equal("apple", Assert.Single(recovered).Text);
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task PreCancellation_WinsBeforeAttemptingToOpenAnExclusivelyLockedFile(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("pre-cancelled-lock" + extension);
        CreateDocument(path, ["apple"]);
        var original = Snapshot(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadInto(path, new ExtractionContext(_ => { }), cancellation.Token));
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task SmallSegmentLimit_RejectsTheWholeDocumentAndKeepsAnotherFileSearchable(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("late-limit" + extension);
        CreateDocument(path, ["ok", "apple"]);
        await File.WriteAllTextAsync(directory.FileFor("good.txt"), "ok");
        var original = Snapshot(path);
        var limits = new SearchResourceLimits { MaxSegmentCharacters = 3 };
        var published = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<SearchResourceLimitException>(() =>
            ReadInto(path, new ExtractionContext(_ => { }, limits: limits), published: published));
        Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), error.LimitName);
        Assert.Equal(3, error.Limit);
        Assert.Empty(published);
        AssertUnchangedAndReleased(path, original);

        var results = new List<FileResult>();
        var issues = new List<SearchIssue>();
        var summary = await new TextSearchEngine(DocumentExtractorCatalog.CreateDefault(), limits).SearchAsync(
            new SearchRequest(directory.Root, ["ok"]),
            results: new InlineProgress<FileResult>(results.Add), issues: new InlineProgress<SearchIssue>(issues.Add));
        Assert.Equal(SearchStatus.Partial, summary.Status);
        Assert.Equal("good.txt", Path.GetFileName(Assert.Single(results).FullPath));
        Assert.Equal(1, summary.Progress.Occurrences);
        Assert.Contains(issues, issue => issue.Path == path && issue.Code == SearchIssueCode.ResourceLimit && issue.Severity == SearchIssueSeverity.Error);
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx", "word:p1")]
    [InlineData(".xlsx", "excel:s1:A1")]
    [InlineData(".pptx", "ppt:s1:o1:r:c:p1")]
    [InlineData(".pdf", "pdf:p1")]
    public async Task ExactSegmentLimit_PreservesTextAndTheExistingLocationId(string extension, string id)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("boundary" + extension);
        CreateDocument(path, ["apple"]);
        var original = Snapshot(path);
        var segments = new List<TextSegment>();
        await ReadInto(path, new ExtractionContext(_ => { }, limits: new SearchResourceLimits { MaxSegmentCharacters = 5 }), published: segments);
        var segment = Assert.Single(segments);
        Assert.Equal("apple", segment.Text);
        Assert.Equal(id, segment.Id);
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task LateMalformedDocument_IsAtomicAndDoesNotPreventAnotherFileFromMatching(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("late-corruption" + extension);
        CreateDocument(path, ["apple", "melon"], malformedLate: true);
        await File.WriteAllTextAsync(directory.FileFor("good.txt"), "apple");
        var original = Snapshot(path);
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["apple"]);
        Assert.Equal(SearchStatus.Partial, scan.Summary.Status);
        Assert.Equal("good.txt", Path.GetFileName(Assert.Single(scan.Results).FullPath));
        Assert.Equal(1, scan.Summary.Progress.Occurrences);
        Assert.Single(scan.Issues, issue => issue.Path == path && issue.Severity == SearchIssueSeverity.Error);
        AssertUnchangedAndReleased(path, original);
    }

    [Fact]
    public async Task EncryptedMarkerPdf_IsWrappedIsolatedAndReleased()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("encrypted-marker.pdf");
        CreatePdf(path, ["apple"], encrypted: true);
        var original = Snapshot(path);
        await File.WriteAllTextAsync(directory.FileFor("good.txt"), "apple");
        var published = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<ExtractionException>(() => ReadInto(path, new ExtractionContext(_ => { }), published: published));
        Assert.NotNull(error.InnerException);
        Assert.Empty(published);
        var scan = await ExtractionTestSupport.SearchAsync(directory.Root, ["apple"]);
        Assert.Equal(SearchStatus.Partial, scan.Summary.Status);
        Assert.Equal("good.txt", Path.GetFileName(Assert.Single(scan.Results).FullPath));
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx", "word/document.xml")]
    [InlineData(".xlsx", "xl/worksheets/sheet1.xml")]
    [InlineData(".pptx", "ppt/slides/slide1.xml")]
    public async Task WrongOpenXmlPartRoot_IsWrappedInsteadOfEscapingIsolation(string extension, string partName)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("wrong-root" + extension);
        CreateDocument(path, ["apple"]);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry(partName)!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(partName).Open(), new UTF8Encoding(false));
            writer.Write("<unexpected/>");
        }
        var original = Snapshot(path);
        var published = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<ExtractionException>(() => ReadInto(path, new ExtractionContext(_ => { }), published: published));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Empty(published);
        AssertUnchangedAndReleased(path, original);
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    [InlineData(".pdf")]
    public async Task ReadOnlyFileAttribute_IsSupportedWithoutModifyingInput(string extension)
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("read-only" + extension);
        CreateDocument(path, ["apple"]);
        var attributes = File.GetAttributes(path);
        var original = Snapshot(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            var segments = new List<TextSegment>();
            await ReadInto(path, new ExtractionContext(_ => { }), published: segments);
            Assert.Equal("apple", Assert.Single(segments).Text);
            AssertUnchangedAndReleased(path, original);
        }
        finally { File.SetAttributes(path, attributes); }
    }

    [Fact]
    public async Task OversizedInput_IsRejectedBeforeCallingTheReaderAndReleasesTheStream()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("sparse-input.docx");
        // Sparse-sized input exercises the real byte gate without allocating a 128 MiB array.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(ReadOnlyExtraction.MaxInputBytes + 1);
        var original = Snapshot(path);
        var calls = 0;
        var published = new List<TextSegment>();
        var error = await Assert.ThrowsAsync<ExtractionException>(async () =>
        {
            await foreach (var segment in ReadOnlyExtraction.Run(path, new ExtractionContext(_ => { }), (_, _, _) =>
            {
                calls++;
                return [Segment("never", "apple")];
            }, CancellationToken.None)) published.Add(segment);
        });
        Assert.Contains("128 MiB", error.Message);
        Assert.Equal(0, calls);
        Assert.Empty(published);
        AssertUnchangedAndReleased(path, original);
    }

    [Fact]
    public void SegmentLimit_UsesUtf16AndDoesNotReplaceEitherCumulativeBudget()
    {
        var utf16 = new BoundedSegmentBuffer(maxCharacters: 100, maxSegments: 3, maxSegmentCharacters: 2);
        utf16.Add(Segment("emoji", "🍉"));
        var error = Assert.Throws<SearchResourceLimitException>(() => utf16.Add(Segment("oversized", "🍉x")));
        Assert.Equal(nameof(SearchResourceLimits.MaxSegmentCharacters), error.LimitName);
        Assert.Single(utf16.Complete());
        var characters = new BoundedSegmentBuffer(maxCharacters: 4, maxSegments: 3, maxSegmentCharacters: 2);
        characters.Add(Segment("a", "ok"));
        characters.Add(Segment("b", "ok"));
        Assert.Throws<ExtractionException>(() => characters.Add(Segment("c", "x")));
        var segments = new BoundedSegmentBuffer(maxCharacters: 100, maxSegments: 2, maxSegmentCharacters: 2);
        segments.Add(Segment("a", "ok"));
        segments.Add(Segment("b", "ok"));
        Assert.Throws<ExtractionException>(() => segments.Add(Segment("c", "x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSegmentBuffer(maxSegmentCharacters: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedSegmentBuffer(maxSegmentCharacters: -1));
    }

    [Fact]
    public void FixedBudgetsAndOpenXmlParserSettings_RemainBounded()
    {
        Assert.Equal(128L * 1024 * 1024, ReadOnlyExtraction.MaxInputBytes);
        Assert.Equal(16L * 1024 * 1024, ReadOnlyExtraction.Settings().MaxCharactersInPart);
        Assert.False(ReadOnlyExtraction.Settings().AutoSave);
        Assert.Equal(32L * 1024 * 1024, BoundedSegmentBuffer.DefaultMaxCharacters);
        Assert.Equal(200_000, BoundedSegmentBuffer.DefaultMaxSegments);
        // Small analogues exercise both cumulative checks without allocating a large document.
        var characters = new BoundedSegmentBuffer(maxCharacters: 4, maxSegments: 3);
        characters.Add(Segment("a", "ok"));
        characters.Add(Segment("b", "ok"));
        Assert.Throws<ExtractionException>(() => characters.Add(Segment("c", "x")));
        var segments = new BoundedSegmentBuffer(maxCharacters: 100, maxSegments: 2);
        segments.Add(Segment("a", "a"));
        segments.Add(Segment("b", "b"));
        Assert.Throws<ExtractionException>(() => segments.Add(Segment("c", "c")));
    }

    private static ITextExtractor Extractor(string path) => ExtractionTestSupport.For(Path.GetExtension(path));

    private static async Task ReadInto(string path, ExtractionContext context, CancellationToken token = default, List<TextSegment>? published = null)
    {
        await foreach (var segment in Extractor(path).ExtractAsync(path, context, token)) published?.Add(segment);
    }

    private static TextSegment Segment(string id, string text) => new(id, text, new SourceLocator(SourceKind.WordParagraph, Paragraph: 1));

    private sealed record FileSnapshot(byte[] Hash, DateTime LastWriteTimeUtc, long Length);

    private static FileSnapshot Snapshot(string path)
    {
        using var stream = File.OpenRead(path);
        return new FileSnapshot(SHA256.HashData(stream), File.GetLastWriteTimeUtc(path), stream.Length);
    }

    private static void AssertUnchangedAndReleased(string path, FileSnapshot expected)
    {
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(expected.Length, exclusive.Length);
            Assert.Equal(expected.Hash, SHA256.HashData(exclusive));
        }
        Assert.Equal(expected.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
        var moved = path + ".moved";
        File.Move(path, moved);
        File.Move(moved, path);
        Assert.Equal(expected.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
    }

    private static void CreateDocument(string path, string[] text, bool malformedLate = false)
    {
        if (Path.GetExtension(path) == ".pdf")
        {
            CreatePdf(path, text, malformedLate);
            return;
        }
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var types = new List<(string Path, string Type)>();
        var root = "";
        switch (Path.GetExtension(path))
        {
            case ".docx":
                root = "word/document.xml";
                types.Add((root, "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"));
                WritePart(root, $"<w:document xmlns:w=\"{WordNamespace}\"><w:body>" +
                    string.Concat(text.Select(value => $"<w:p><w:r><w:t>{value}</w:t></w:r></w:p>")) +
                    (malformedLate ? "<w:p></w:body>" : "</w:body></w:document>"));
                break;
            case ".xlsx":
                root = "xl/workbook.xml";
                types.Add((root, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
                WritePart(root, $"<workbook xmlns=\"{SheetNamespace}\" xmlns:r=\"{RelationNamespace}\"><sheets>" +
                    string.Concat(text.Select((_, index) => $"<sheet name=\"Sheet{index + 1}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>")) + "</sheets></workbook>");
                WritePart("xl/_rels/workbook.xml.rels", Relationships(text.Select((_, index) =>
                    ($"rId{index + 1}", "worksheet", $"worksheets/sheet{index + 1}.xml"))));
                for (var index = 0; index < text.Length; index++)
                {
                    var part = $"xl/worksheets/sheet{index + 1}.xml";
                    types.Add((part, "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
                    WritePart(part, malformedLate && index == text.Length - 1 ? "<worksheet>" :
                        $"<worksheet xmlns=\"{SheetNamespace}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"str\"><v>{text[index]}</v></c></row></sheetData></worksheet>");
                }
                break;
            case ".pptx":
                root = "ppt/presentation.xml";
                types.Add((root, "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"));
                WritePart(root, $"<p:presentation xmlns:p=\"{PresentationNamespace}\" xmlns:r=\"{RelationNamespace}\"><p:sldIdLst>" +
                    string.Concat(text.Select((_, index) => $"<p:sldId id=\"{256 + index}\" r:id=\"rId{index + 1}\"/>")) + "</p:sldIdLst></p:presentation>");
                WritePart("ppt/_rels/presentation.xml.rels", Relationships(text.Select((_, index) =>
                    ($"rId{index + 1}", "slide", $"slides/slide{index + 1}.xml"))));
                for (var index = 0; index < text.Length; index++)
                {
                    var part = $"ppt/slides/slide{index + 1}.xml";
                    types.Add((part, "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"));
                    WritePart(part, malformedLate && index == text.Length - 1 ? "<p:sld>" :
                        $"<p:sld xmlns:p=\"{PresentationNamespace}\" xmlns:a=\"{DrawingNamespace}\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"Original\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{text[index]}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>");
                }
                break;
            default: throw new ArgumentException("Unsupported test format.", nameof(path));
        }
        WritePart("_rels/.rels", Relationships([("rId1", "officeDocument", root)]));
        WritePart("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            string.Concat(types.Select(item => $"<Override PartName=\"/{item.Path}\" ContentType=\"{item.Type}\"/>")) + "</Types>");
        return;

        void WritePart(string name, string xml)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    private static string Relationships(IEnumerable<(string Id, string Type, string Target)> relations) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(relations.Select(item => $"<Relationship Id=\"{item.Id}\" Type=\"{RelationNamespace}/{item.Type}\" Target=\"{item.Target}\"/>")) + "</Relationships>";

    private static void CreatePdf(string path, string[] text, bool malformedLate = false, bool encrypted = false)
    {
        // Original tiny PDF with explicit xref offsets; no Office or external fixtures are involved.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + text.Length + " /Kids [" + string.Join(" ", text.Select((_, index) => $"{4 + 2 * index} 0 R")) + "] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (var index = 0; index < text.Length; index++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * index} 0 R >>");
            var content = $"BT /F1 12 Tf 40 700 Td ({text[index]}) Tj ET\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)}" +
                (malformedLate && index == text.Length - 1 ? " /Filter /UnknownPhase4Filter" : "") + $" >>\nstream\n{content}endstream");
        }
        if (encrypted)
            objects.Add("<< /Filter /Standard /V 1 /R 2 /Length 40 /P -4 /O <0000000000000000000000000000000000000000000000000000000000000000> /U <0000000000000000000000000000000000000000000000000000000000000000> >>");
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R");
        if (encrypted) pdf.Append(CultureInfo.InvariantCulture, $" /Encrypt {objects.Count} 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");
        pdf.Append(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
    }
}
