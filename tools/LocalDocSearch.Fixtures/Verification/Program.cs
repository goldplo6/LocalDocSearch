using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using LocalDocSearch.Core;
using LocalDocSearch.Extractors;
using LocalDocSearch.Fixtures;
using UglyToad.PdfPig;
using W = DocumentFormat.OpenXml.Wordprocessing;

if (args.Length != 1) throw new ArgumentException("Usage: LocalDocSearch.Fixtures.Verification <long-documents-directory>");
string root = Path.GetFullPath(args[0]);
string[] names = [LongDocuments.PdfName, LongDocuments.WordName, LongDocuments.TextName, LongDocuments.CsvName];
var before = names.Append("README").ToDictionary(name => name, name => Snapshot(Path.Combine(root, name)));

int pdfPages;
using (var pdf = PdfDocument.Open(Path.Combine(root, LongDocuments.PdfName)))
{
    pdfPages = pdf.NumberOfPages;
    Require(pdfPages == 200, "PDF must have 200 real pages.");
    for (int page = 1; page <= 200; page++)
    {
        string text = pdf.GetPage(page).Text;
        Require(text.Contains($"LD_PDF_PAGE_{page:000}", StringComparison.Ordinal), $"PDF page marker missing at {page}.");
        Require(text.Length > 220, $"PDF page {page} must contain original body text.");
    }
}

int manualPageBreaks;
int bodyParagraphs;
using (var word = WordprocessingDocument.Open(Path.Combine(root, LongDocuments.WordName), false))
{
    Require(!new OpenXmlValidator().Validate(word).Any(), "DOCX must pass OpenXmlValidator.");
    var body = word.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX body is missing.");
    manualPageBreaks = body.Descendants<W.Break>().Count(item => item.Type?.Value == W.BreakValues.Page);
    bodyParagraphs = body.Elements<W.Paragraph>().Count();
    Require(manualPageBreaks == 199 && bodyParagraphs == 2400, "DOCX needs 199 manual breaks and 2400 paragraphs.");
    int page = 1;
    var pageText = new System.Text.StringBuilder();
    foreach (var paragraph in body.Elements<W.Paragraph>())
    {
        if (paragraph.Descendants<W.Break>().Any(item => item.Type?.Value == W.BreakValues.Page))
        {
            VerifyWordPage(page, pageText.ToString());
            page++;
            pageText.Clear();
        }
        pageText.Append(paragraph.InnerText);
    }
    VerifyWordPage(page, pageText.ToString());
    Require(page == 200, "DOCX must have 200 manual content pages.");
}

var evidence = new List<object>();
var extractors = new ITextExtractor[] { new PdfTextExtractor(), new WordTextExtractor(), new PlainTextExtractor(), new CsvTextExtractor() };
for (int file = 0; file < names.Length; file++)
{
    string name = names[file];
    string path = Path.Combine(root, name);
    var repeated = new List<TextSegment>();
    var tail = new List<TextSegment>();
    int segments = 0;
    TextSegment? last = null;
    var warnings = new List<string>();
    var anchors = new Dictionary<string, string>();
    string[] expectedAnchors = file switch
    {
        0 => ["LD_PDF_P150_ANCHOR", "LD_PDF_P200_ANCHOR", "LD_PDF_END"],
        1 => ["LD_DOCX_P150_ANCHOR", "LD_DOCX_P200_ANCHOR", "LD_DOCX_END"],
        2 => ["LD_TXT_L075000_ANCHOR", "LD_TXT_L100000_ANCHOR", "LD_TXT_END"],
        _ => ["LD_CSV_R15000_ANCHOR", "LD_CSV_R20000_ANCHOR", "LD_CSV_END"]
    };
    await foreach (var segment in extractors[file].ExtractAsync(path, new ExtractionContext(warnings.Add)))
    {
        segments++;
        last = segment;
        if (segment.Text.Contains(LongDocuments.RepeatedTerm, StringComparison.Ordinal)) repeated.Add(segment);
        if (segment.Text.Contains(LongDocuments.TailTerm, StringComparison.Ordinal)) tail.Add(segment);
        foreach (string marker in expectedAnchors)
            if (segment.Text.Contains(marker, StringComparison.Ordinal))
                Require(anchors.TryAdd(marker, segment.Id), $"Marker {marker} must occur in just one segment.");
    }
    Require(repeated.Count == 2, $"{name} must have two independent repeated-term segments.");
    Require(tail.Count == 1 && tail[0].Id == last?.Id, $"{name}: complete tail term must be in final segment.");
    Require(anchors.Count == 3, $"{name}: all ASCII anchors must round-trip.");
    Require(repeated[0].Text.Contains("雨后东棚复核完成", StringComparison.Ordinal), "Earlier context missing.");
    Require(repeated[1].Text.Contains("终卷南库封存完成", StringComparison.Ordinal), "Later context missing.");
    int expectedSegments = file switch { 0 => 200, 1 => 2400, 2 => 100000, _ => 60003 };
    Require(segments == expectedSegments, $"Unexpected segment count for {name}: {segments}.");
    switch (file)
    {
        case 0:
            Require(repeated[0].Source.Page == 150 && repeated[1].Source.Page == 200, "PDF source pages must be 150/200.");
            break;
        case 1:
            Require(repeated[0].Source.Paragraph == 1798 && repeated[1].Source.Paragraph == 2398, "Word repeat source paragraphs must be 1798/2398.");
            Require(tail[0].Source.Paragraph == 2400 && tail[0].Text.Contains("LD_DOCX_END", StringComparison.Ordinal), "Word tail must be intact in paragraph 2400.");
            break;
        case 2:
            Require(repeated[0].Source.Line == 75000 && repeated[1].Source.Line == 100000, "TXT source lines must be 75000/100000.");
            break;
        case 3:
            Require(repeated[0].Source.Record == 15001 && repeated[1].Source.Record == 20001 && repeated.All(item => item.Source.Field == 3), "CSV source records must include header and point at field 3.");
            Require(repeated[0].Source.Line == 15001 && repeated[1].Source.Line == 20001, "CSV physical source lines must be 15001/20001.");
            break;
    }
    evidence.Add(new
    {
        file = name,
        bytes = before[name].Bytes,
        sha256 = before[name].Sha256,
        segments,
        repeatedMatches = repeated.Select(item => new { segment = item.Id, item.Source, text = item.Text.Trim() }).ToArray(),
        tail = new { segment = tail[0].Id, tail[0].Source, text = tail[0].Text.Trim() },
        anchors,
        warnings
    });
}

Require(File.ReadLines(Path.Combine(root, LongDocuments.TextName)).LongCount() == 100000, "TXT physical line count mismatch.");
Require(File.ReadLines(Path.Combine(root, LongDocuments.CsvName)).LongCount() == 20001, "CSV physical line count mismatch.");
var engine = new TextSearchEngine(extractors);
var repeatedResults = await Search(LongDocuments.RepeatedTerm, expectedFiles: 4, expectedOccurrences: 8);
Require(repeatedResults.All(item => item.Matches.Count == 2), "Search must publish two occurrences per file.");
Require(repeatedResults.SelectMany(item => item.Matches).All(item => item.SourceSegmentId is not null), "Every match must keep its source segment ID.");
var tailResults = await Search(LongDocuments.TailTerm, expectedFiles: 4, expectedOccurrences: 4);
Require(tailResults.All(item => item.Matches.Count == 1), "Search must publish one intact tail occurrence per file.");
await Search("苹果", expectedFiles: 0, expectedOccurrences: 0);
var asciiResults = await Search("LD_PDF_P150_ANCHOR", expectedFiles: 1, expectedOccurrences: 1);
Require(asciiResults[0].Matches[0].Location.Page == 150, "ASCII native verification must preserve PDF source page 150.");
// A query spanning two separate Word paragraphs must never turn into a fabricated match.
await Search("LD_DOCX_P200_ANCHOR长文末尾定位", expectedFiles: 0, expectedOccurrences: 0);
foreach (string name in before.Keys)
    Require(Snapshot(Path.Combine(root, name)) == before[name], $"Verification modified fixture: {name}");

Console.WriteLine(JsonSerializer.Serialize(new
{
    success = true,
    directory = root,
    pdfPages,
    wordManualContentPages = manualPageBreaks + 1,
    wordManualPageBreaks = manualPageBreaks,
    wordBodyParagraphs = bodyParagraphs,
    textLines = 100000,
    csvDataRecords = 20000,
    csvPhysicalLines = 20001,
    totalArtifactBytes = names.Sum(name => before[name].Bytes),
    search = new { repeatedFiles = 4, repeatedOccurrences = 8, tailFiles = 4, tailOccurrences = 4, absentApple = 0, crossParagraphFalseMatches = 0 },
    files = evidence,
    artifactsUnchanged = true
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

async Task<List<FileResult>> Search(string term, int expectedFiles, int expectedOccurrences)
{
    var results = new List<FileResult>();
    var issues = new List<SearchIssue>();
    var summary = await engine.SearchAsync(new SearchRequest(root, [term], ContextCharacters: 40),
        results: new ImmediateProgress<FileResult>(results.Add), issues: new ImmediateProgress<SearchIssue>(issues.Add));
    Require(summary.Status is SearchStatus.Completed or SearchStatus.Partial, $"Search failed for {term}: {summary.Status}");
    Require(issues.All(issue => issue.Severity != SearchIssueSeverity.Error), $"Search errors for {term}.");
    Require(results.Count == expectedFiles && results.Sum(item => item.Matches.Count) == expectedOccurrences,
        $"Search count mismatch for {term}: files={results.Count}; matches={results.Sum(item => item.Matches.Count)}");
    return results;
}

static void VerifyWordPage(int page, string text)
{
    Require(text.Contains($"LD_DOCX_PAGE_{page:000}", StringComparison.Ordinal), $"DOCX content-page marker missing at {page}.");
    Require(text.Length > 220, $"DOCX page {page} must contain original body text.");
}

static (long Bytes, DateTime LastWriteTimeUtc, string Sha256) Snapshot(string path)
{
    var info = new FileInfo(path);
    using var stream = File.OpenRead(path);
    return (info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
