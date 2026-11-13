using System.Globalization;
using System.Text;
using UglyToad.PdfPig;

namespace LocalDocSearch.Fixtures;

internal static class PdfFixture
{
    // This deliberately small PDF writer exists only for original test fixtures.
    // It uses a standard, unembedded CJK font; no Windows font is copied or needed
    // during generation. The ToUnicode CMap explicitly maps every used character.
    // Readers render STSong-Light through their standard CJK font support.
    internal static void Create(string path)
    {
        string[] pageText = [
            "第一页：苹果",
            "第二页：普通文字，字面量：.*",
            "第三页：西瓜"
        ];

        Create(path, pageText.Select(text => (IReadOnlyList<string>)new[] { text }).ToArray(), 20, 72, 760);
    }

    internal static void Create(string path, IReadOnlyList<IReadOnlyList<string>> pages,
        int fontSize = 12, int left = 55, int top = 780)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF fixture needs at least one page.", nameof(pages));
        var pageText = pages.Select(lines => string.Concat(lines)).ToArray();
        // A bfchar block contains at most 100 entries, including for long Chinese documents.
        string mappings = string.Join("\n", pageText.SelectMany(text => text).Distinct().Order().Chunk(100)
            .Select(characters => $"{characters.Length} beginbfchar\n" +
                string.Join("\n", characters.Select(character => $"<{(int)character:X4}> <{(int)character:X4}>")) +
                "\nendbfchar"));
        string cmap = $"""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /LocalDocSearch-UCS def
            /CMapType 2 def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            {mappings}
            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
            """ + "\n";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (zh-CN) >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages.Count).Select(index => $"{7 + index * 2} 0 R"))}] /Count {pages.Count} >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light " +
                "/Encoding /UniGB-UCS2-H /DescendantFonts [4 0 R] /ToUnicode 6 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 4 >> " +
                "/FontDescriptor 5 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /STSong-Light /Flags 6 " +
                "/FontBBox [-25 -254 1000 880] /ItalicAngle 0 /Ascent 880 " +
                "/Descent -120 /CapHeight 880 /StemV 80 >>",
            StreamObject(cmap)
        };

        foreach (var lines in pages)
        {
            int pageObject = objects.Count + 1;
            int contentObject = pageObject + 1;
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>");
            var content = new StringBuilder($"BT\n/F1 {fontSize} Tf\n{fontSize + 9} TL\n1 0 0 1 {left} {top} Tm\n");
            foreach (string line in lines)
            {
                string hex = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(line));
                content.Append($"<{hex}> Tj\nT*\n");
            }
            content.Append("ET\n");
            objects.Add(StreamObject(content.ToString()));
        }

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteAscii(file, "%PDF-1.7\n%LocalDocSearch-original-fixture\n");
            var offsets = new List<long> { 0 };
            for (int index = 0; index < objects.Count; index++)
            {
                offsets.Add(file.Position);
                WriteAscii(file, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
            }
            long xref = file.Position;
            WriteAscii(file, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
            foreach (long offset in offsets.Skip(1))
            {
                WriteAscii(file, offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            }
            WriteAscii(file, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }

        // Read back the actual file, rather than trusting source strings or bytes.
        // Exact per-page and overall counts catch font/CMap/extraction regressions.
        using var pdf = PdfDocument.Open(path);
        if (pdf.NumberOfPages != pages.Count)
        {
            throw new InvalidDataException($"Generated PDF must contain exactly {pages.Count} pages.");
        }
        string[] actual = Enumerable.Range(1, pages.Count).Select(number => pdf.GetPage(number).Text).ToArray();
        if (!actual.SequenceEqual(pageText))
        {
            throw new InvalidDataException("Generated PDF text did not round-trip through PdfPig.");
        }
    }

    private static string StreamObject(string content) =>
        $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";

    private static void WriteAscii(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
}
