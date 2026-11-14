using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LocalDocSearch.Core;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace LocalDocSearch.Extractors.Tests;

public sealed class ExcelCellAddressTests
{
    // Independent reference: keep the original algorithm only in tests.
    private static readonly Regex OriginalAddressPattern = new(
        "^([A-Za-z]{1,3})([1-9][0-9]{0,6})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Theory]
    [InlineData(null, false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData("A1", true, 1, 1)]
    [InlineData("a1", true, 1, 1)]
    [InlineData("z9", true, 9, 26)]
    [InlineData("Aa10", true, 10, 27)]
    [InlineData("aZ99", true, 99, 52)]
    [InlineData("zz1", true, 1, 702)]
    [InlineData("aaa1", true, 1, 703)]
    [InlineData("xfd1048576", true, 1048576, 16384)]
    [InlineData("XFC1048576", true, 1048576, 16383)]
    [InlineData("XFE1", false, 1, 16385)]
    [InlineData("ZZZ1", false, 1, 18278)]
    [InlineData("AAAA1", false, 0, 0)]
    [InlineData("A1048575", true, 1048575, 1)]
    [InlineData("A1048576", true, 1048576, 1)]
    [InlineData("A1048577", false, 1048577, 0)]
    [InlineData("A9999999", false, 9999999, 0)]
    [InlineData("XFE1048577", false, 1048577, 0)]
    [InlineData("A10000000", false, 0, 0)]
    [InlineData("A2147483647", false, 0, 0)]
    [InlineData("A2147483648", false, 0, 0)]
    [InlineData("A4294967296", false, 0, 0)]
    [InlineData("A0", false, 0, 0)]
    [InlineData("A00", false, 0, 0)]
    [InlineData("A01", false, 0, 0)]
    [InlineData("A0000001", false, 0, 0)]
    [InlineData("0", false, 0, 0)]
    [InlineData("01", false, 0, 0)]
    [InlineData("1", false, 0, 0)]
    [InlineData("A", false, 0, 0)]
    [InlineData("ABC", false, 0, 0)]
    [InlineData("1A", false, 0, 0)]
    [InlineData("A1B", false, 0, 0)]
    [InlineData("A1B2", false, 0, 0)]
    [InlineData("$A$1", false, 0, 0)]
    [InlineData("$A1", false, 0, 0)]
    [InlineData("A$1", false, 0, 0)]
    [InlineData("Sheet1!A1", false, 0, 0)]
    [InlineData("A1:B2", false, 0, 0)]
    [InlineData(" A1", false, 0, 0)]
    [InlineData("A1 ", false, 0, 0)]
    [InlineData("\tA1", false, 0, 0)]
    [InlineData("A1\t", false, 0, 0)]
    [InlineData("A+1", false, 0, 0)]
    [InlineData("A-1", false, 0, 0)]
    [InlineData("A1.0", false, 0, 0)]
    [InlineData("A1,000", false, 0, 0)]
    [InlineData("A1e1", false, 0, 0)]
    [InlineData("A١", false, 0, 0)]
    [InlineData("A１", false, 0, 0)]
    [InlineData("Ａ1", false, 0, 0)]
    [InlineData("é1", false, 0, 0)]
    [InlineData("İ1", false, 0, 0)]
    [InlineData("ı1", false, 0, 0)]
    [InlineData("K1", false, 0, 0)]
    [InlineData("西1", false, 0, 0)]
    [InlineData("😀1", false, 0, 0)]
    [InlineData("A1\0", false, 0, 0)]
    [InlineData("A1\n", true, 1, 1)]
    [InlineData("xFd1048576\n", true, 1048576, 16384)]
    [InlineData("XFE1\n", false, 1, 16385)]
    [InlineData("A1048577\n", false, 1048577, 0)]
    [InlineData("A1\n\n", false, 0, 0)]
    [InlineData("A1\r", false, 0, 0)]
    [InlineData("A1\r\n", false, 0, 0)]
    [InlineData("\nA1", false, 0, 0)]
    [InlineData("A\n1", false, 0, 0)]
    [InlineData("A1\u0085", false, 0, 0)]
    [InlineData("A1\u2028", false, 0, 0)]
    [InlineData("A1\u2029", false, 0, 0)]
    public void Coordinates_PreserveSuccessAndBothFailureOutputs(string? address, bool success, int row, int column)
    {
        Assert.Equal((success, row, column), Parse(address));
        AssertMatchesOriginal(address);
    }

    [Fact]
    public void Differential_ExhaustiveShortAsciiAndUnicodeInputs()
    {
        const string alphabet = "AaZz019$ \r\né１";
        AssertMatchesOriginal(null);
        ComparePrefixes(string.Empty, 4);

        void ComparePrefixes(string prefix, int remaining)
        {
            AssertMatchesOriginal(prefix);
            if (remaining == 0) return;
            foreach (var character in alphabet) ComparePrefixes(prefix + character, remaining - 1);
        }
    }

    [Fact]
    public void Differential_AllOneToThreeLetterColumnsAtRowBoundaries()
    {
        // All 18,278 columns expressible by the old syntax, including columns > XFD.
        for (var column = 1; column <= 18278; column++)
        {
            var letters = ColumnLetters(column);
            AssertMatchesOriginal(letters + "1");
            AssertMatchesOriginal(letters.ToLowerInvariant() + "1048576");
            AssertMatchesOriginal(letters + "1048577\n");
        }
    }

    [Fact]
    public void Differential_NumberFormsAndAnchors()
    {
        string[] columns = ["", "A", "a", "i", "Z", "Aa", "XfD", "XFE", "ZZZ", "AAAA", "Ａ", "İ", "ı", "K", "é", "\0", "😀"];
        string[] rows = ["", "0", "00", "01", "0000001", "1", "9", "10", "999999", "1000000", "1048575", "1048576", "1048577", "9999999", "10000000", "2147483647", "2147483648", "-1", "+1", "1.0", "١", "１", "1x"];
        string[] endings = ["", "\n", "\n\n", "\r\n", " ", "\0"];
        foreach (var column in columns)
            foreach (var row in rows)
                foreach (var ending in endings)
                    AssertMatchesOriginal(column + row + ending);
    }

    [Fact]
    public void ExtremeLengthInputs_PreserveGrammarFailureOutputsWithoutOverflow()
    {
        var letters = new string('A', 100_000);
        var digits = new string('9', 100_000);
        string[] addresses = [letters, letters + "1", "A" + digits, "A" + digits + "\n", "A1" + new string('\n', 100_000), "A" + new string('0', 100_000) + "1"];
        foreach (var address in addresses)
        {
            Assert.Equal((false, 0, 0), Parse(address));
            AssertMatchesOriginal(address);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void AsciiCoordinates_AreIndependentOfCurrentCulture(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            Assert.Equal((true, 19, 9), Parse("i19"));
            Assert.Equal((true, 1048576, 16384), Parse("xFd1048576\n"));
            AssertMatchesOriginal("i19");
            AssertMatchesOriginal("A١");
            AssertMatchesOriginal("İ1");
            AssertMatchesOriginal("A1048577");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CustomNumberFormat_PreservesOriginalIntegerParsingInsteadOfImposingNewRowRules()
    {
        var previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "1";
        culture.NumberFormat.NegativeSign = "2";
        try
        {
            CultureInfo.CurrentCulture = culture;
            Assert.Equal((true, 9, 1), Parse("A19"));
            Assert.Equal((true, -9, 1), Parse("A29"));
            Assert.Equal((true, 0, 1), Parse("A10"));
            Assert.Equal((true, 0, 1), Parse("A20"));
            Assert.Equal((false, 0, 0), Parse("A1"));
            Assert.Equal((false, 0, 0), Parse("A2"));
            foreach (var address in new[] { "A19", "A29", "A1", "A2", "A10", "A20", "A1048576", "A2999999", "XFE29", "a19\n", "A01" })
                AssertMatchesOriginal(address);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Extraction_InvalidOrMissingReferencesSkipWithoutPositionalFallback()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("address-contract.xlsx");
        string?[] invalid = [null, "", "A0", "XFE1", "A1048577", "A01", " A1", "AAAA1", "A2147483648"];
        var cells = invalid.Select(address => TextCell(address, "skip")).ToList();
        cells.Add(TextCell("a1", "first"));
        cells.Add(TextCell("b3", "second"));
        cells.Add(TextCell("XFD1048576", "last"));
        WriteWorkbook(path, cells);

        var warnings = new List<string>();
        var segments = await ExtractionTestSupport.ReadAsync(path, warnings);
        Assert.Equal(9, warnings.Count(message => message.Contains("未猜测位置", StringComparison.Ordinal)));
        Assert.Equal(new[] { "first", "second", "last" }, segments.Select(segment => segment.Text));
        Assert.Equal(new[] { "A1", "B3", "XFD1048576" }, segments.Select(segment => segment.Source.Address));
        Assert.Equal(new[] { "excel:s1:A1", "excel:s1:B3", "excel:s1:XFD1048576" }, segments.Select(segment => segment.Id));
        Assert.Collection(segments,
            segment => { Assert.Equal(1, segment.Source.Row); Assert.Equal(1, segment.Source.Cell); },
            segment => { Assert.Equal(3, segment.Source.Row); Assert.Equal(2, segment.Source.Cell); },
            segment => { Assert.Equal(1048576, segment.Source.Row); Assert.Equal(16384, segment.Source.Cell); });
    }

    [Fact]
    public async Task Extraction_DuplicateReferencesRemainCaseInsensitive()
    {
        using var directory = new TestDirectory();
        var path = directory.FileFor("duplicate-address.xlsx");
        WriteWorkbook(path, [TextCell("a1", "first"), TextCell("A1", "second")]);
        var error = await Assert.ThrowsAsync<ExtractionException>(() => ExtractionTestSupport.ReadAsync(path));
        Assert.Contains("重复单元格坐标 A1", error.Message);
    }

    private static (bool Success, int Row, int Column) Parse(string? address)
    {
        var success = ExcelTextExtractor.TryCoordinates(address, out var row, out var column);
        return (success, row, column);
    }

    private static void AssertMatchesOriginal(string? address)
    {
        var success = OriginalTryCoordinates(address, out var row, out var column);
        Assert.Equal((success, row, column), Parse(address));
    }

    private static bool OriginalTryCoordinates(string? address, out int row, out int column)
    {
        row = column = 0;
        if (address is null) return false;
        var match = OriginalAddressPattern.Match(address);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out row) || row > 1048576) return false;
        foreach (var letter in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + letter - 'A' + 1;
        return column <= 16384;
    }

    private static string ColumnLetters(int column)
    {
        Span<char> letters = stackalloc char[3];
        var start = letters.Length;
        while (column > 0)
        {
            column--;
            letters[--start] = (char)('A' + column % 26);
            column /= 26;
        }
        return new string(letters[start..]);
    }

    private static S.Cell TextCell(string? address, string value)
    {
        var cell = new S.Cell { DataType = S.CellValues.String, CellValue = new S.CellValue(value) };
        if (address is not null) cell.CellReference = address;
        return cell;
    }

    private static void WriteWorkbook(string path, IEnumerable<S.Cell> cells)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook(new S.Sheets());
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        // Deliberately disagree with the cell references: RowIndex is not a fallback.
        worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(cells) { RowIndex = 99U }));
        workbook.Workbook.Sheets!.Append(new S.Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1U, Name = "Coordinates" });
    }
}
