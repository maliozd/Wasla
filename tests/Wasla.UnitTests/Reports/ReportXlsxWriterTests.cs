using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>The Excel export (WAS-184): a valid workbook with typed cells, localized sheets and nothing executable.</summary>
public sealed class ReportXlsxWriterTests
{
    private sealed class Workbook : IDisposable
    {
        private readonly MemoryStream _stream;

        public Workbook(byte[] bytes)
        {
            _stream = new MemoryStream(bytes);
            Package = SpreadsheetDocument.Open(_stream, false);
        }

        public SpreadsheetDocument Package { get; }

        public IReadOnlyList<Sheet> Sheets => Package.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToList();

        public Worksheet Sheet(int index) => ((WorksheetPart)Package.WorkbookPart!.GetPartById(Sheets[index].Id!)).Worksheet!;

        public CellFormat Format(Cell cell) =>
            Package.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.CellFormats!.Elements<CellFormat>().ElementAt((int)(cell.StyleIndex?.Value ?? 0));

        public string? NumberFormat(Cell cell)
        {
            var id = Format(cell).NumberFormatId!.Value;
            var custom = Package.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.NumberingFormats?.Elements<NumberingFormat>()
                .FirstOrDefault(f => f.NumberFormatId!.Value == id);
            return custom?.FormatCode?.Value ?? id switch { 3 => "#,##0", 49 => "@", _ => id.ToString(CultureInfo.InvariantCulture) };
        }

        public void Dispose()
        {
            Package.Dispose();
            _stream.Dispose();
        }
    }

    private static Workbook Open(ReportExportDocument document) => new(ReportXlsxWriter.Write(document));

    private static List<List<Cell>> Rows(Worksheet sheet) =>
        sheet.GetFirstChild<SheetData>()!.Elements<Row>().Select(r => r.Elements<Cell>().ToList()).ToList();

    private static string Text(Cell cell)
    {
        Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
        return cell.InlineString!.Text!.Text;
    }

    private static decimal Number(Cell cell)
    {
        Assert.True(cell.DataType is null || cell.DataType.Value == CellValues.Number, $"{cell.CellReference} is not a number cell.");
        return decimal.Parse(cell.CellValue!.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Workbook_IsValidOfficeOpenXml_WithThreeSheets_AndNothingExecutable()
    {
        using var workbook = Open(Document(Known()));

        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(workbook.Package, TestContext.Current.CancellationToken).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));

        Assert.Equal(["Summary", "Platforms", "Daily"], workbook.Sheets.Select(s => s.Name!.Value));
        Assert.Equal(SpreadsheetDocumentType.Workbook, workbook.Package.DocumentType);
        var parts = workbook.Package.GetAllParts().ToList();
        Assert.DoesNotContain(parts, p => p is VbaProjectPart or ExternalWorkbookPart or SharedStringTablePart or CalculationChainPart);
        Assert.Empty(workbook.Package.WorkbookPart!.ExternalRelationships);
        for (var i = 0; i < 3; i++)
        {
            Assert.Empty(workbook.Sheet(i).Descendants<CellFormula>());
            Assert.Empty(workbook.Sheet(i).Descendants<Hyperlink>());
        }
    }

    [Fact]
    public void Summary_HoldsTheExactFigures_AsNumberCells_WithTheirFormats()
    {
        using var workbook = Open(Document(Known()));
        var rows = Rows(workbook.Sheet(0));

        Assert.Equal("Orders report", Text(rows[0][0]));
        Cell Value(string measure) => rows.Single(r => r.Count == 3 && r[0].DataType?.Value == CellValues.InlineString && Text(r[0]) == measure)[1];
        Assert.Equal(11m, Number(Value("Total orders")));
        Assert.Equal("#,##0", workbook.NumberFormat(Value("Total orders")));
        Assert.Equal(2645.05m, Number(Value("Completed order value (TRY)")));
        Assert.Equal("#,##0.00", workbook.NumberFormat(Value("Completed order value (TRY)")));
        Assert.Equal(377.86m, Number(Value("Average completed order (TRY)")));
        Assert.Equal(2907.55m, Number(Value("All order value (TRY)")));
        Assert.Equal(1m / 11m, Number(Value("Cancellation or rejection rate")), 10);
        Assert.Equal("0.0%", workbook.NumberFormat(Value("Cancellation or rejection rate")));

        // Details: dates and the generation time are date cells, the rest text.
        Cell Detail(string label) => rows.Single(r => r.Count == 2 && Text(r[0]) == label)[1];
        Assert.Equal(new DateTime(2026, 10, 6).ToOADate(), (double)Number(Detail("First day")));
        Assert.Equal("yyyy-mm-dd", workbook.NumberFormat(Detail("First day")));
        Assert.Equal(new DateTime(2026, 11, 10, 14, 10, 5).ToOADate(), (double)Number(Detail("Created")), 6);
        Assert.Equal("yyyy-mm-dd hh:mm", workbook.NumberFormat(Detail("Created")));
        Assert.Equal("Europe/Istanbul", Text(Detail("Time zone")));
        Assert.Equal("TRY", Text(Detail("Currency")));
    }

    [Fact]
    public void DataSheets_HoldEveryRow_WithFrozenHeaders_FiltersAndTypedCells()
    {
        using var workbook = Open(Document(Known()));

        var platforms = Rows(workbook.Sheet(1));
        Assert.Equal(4, platforms.Count);
        Assert.Equal("Platform", Text(platforms[0][0]));
        Assert.Equal(["Trendyol GO", "Yemeksepeti", "GetirYemek"], platforms.Skip(1).Select(r => Text(r[0])));
        Assert.Equal([5m, 4m, 1120.29m], [Number(platforms[1][1]), Number(platforms[2][1]), Number(platforms[1][10])]);
        Assert.Equal("@", workbook.NumberFormat(platforms[1][0]));

        var days = Rows(workbook.Sheet(2));
        Assert.Equal(4, days.Count);
        Assert.Equal(new DateTime(2026, 10, 7).ToOADate(), (double)Number(days[2][0]));
        Assert.Equal("yyyy-mm-dd", workbook.NumberFormat(days[2][0]));
        // A day without orders: no rate and no average (empty cells), never 0.
        Assert.Null(days[2][7].CellValue);
        Assert.Null(days[2][9].CellValue);
        Assert.Equal(0m, Number(days[2][8]));

        for (var i = 1; i <= 2; i++)
        {
            var sheet = workbook.Sheet(i);
            var pane = sheet.Descendants<Pane>().Single();
            Assert.Equal(PaneStateValues.Frozen, pane.State!.Value);
            Assert.Equal("B2", pane.TopLeftCell!.Value);
            Assert.Equal("A1:K4", sheet.GetFirstChild<AutoFilter>()!.Reference!.Value);
        }

        // Sums of the rows are the summary; the workbook stores values, not formulas.
        Assert.Equal(11m, platforms.Skip(1).Sum(r => Number(r[1])));
        Assert.Equal(2907.55m, days.Skip(1).Sum(r => Number(r[10])));
    }

    [Theory]
    [InlineData("tr-TR", "Özet", "Platformlar", "Günlük", false)]
    [InlineData("ar-SA", "الملخص", "المنصات", "يومي", true)]
    [InlineData("ru-RU", "Сводка", "Платформы", "По дням", false)]
    public void Sheets_AreNamedInTheUsersLanguage_AndReadRightToLeftInArabic(string culture, string summary, string platforms, string daily, bool rtl)
    {
        using var workbook = Open(Document(Known(), culture, "Şükrü — مطعم — Ёлка"));

        Assert.Equal([summary, platforms, daily], workbook.Sheets.Select(s => s.Name!.Value));
        for (var i = 0; i < 3; i++)
            Assert.Equal(rtl, workbook.Sheet(i).Descendants<SheetView>().Single().RightToLeft?.Value ?? false);
        Assert.Contains(Rows(workbook.Sheet(0)), r => r.Count == 2 && Text(r[1]) == "Şükrü — مطعم — Ёлка");
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(workbook.Package, TestContext.Current.CancellationToken).ToList();
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://example.test\",\"x\")")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("+90 555")]
    public void FormulaLikeNames_AreTextCells_InTextFormat(string name)
    {
        using var workbook = Open(Document(Known(), restaurant: name));

        var cell = Rows(workbook.Sheet(0)).Single(r => r.Count == 2 && Text(r[0]) == "Restaurant")[1];
        Assert.Equal(name, Text(cell));
        Assert.Equal("@", workbook.NumberFormat(cell));
        Assert.Null(cell.CellFormula);
    }

    [Fact]
    public void EmptyPeriod_HasAHeaderOnlyPlatformSheet_AndZeroDays()
    {
        using var workbook = Open(Document(Empty()));

        Assert.Single(Rows(workbook.Sheet(1)));
        Assert.Equal("A1:K1", workbook.Sheet(1).GetFirstChild<AutoFilter>()!.Reference!.Value);
        Assert.Equal(4, Rows(workbook.Sheet(2)).Count);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2016).Validate(workbook.Package, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new[] { "Summary", "Platforms", "Daily" }, new[] { "Summary", "Platforms", "Daily" })]
    [InlineData(new[] { "a/b:c*?[x]", "'quoted'", "" }, new[] { "abcx", "quoted", "Sheet3" })]
    [InlineData(new[] { "Same", "same", "This sheet name is far longer than Excel allows" }, new[] { "Same", "Sheet2", "This sheet name is far longer t" })]
    public void SheetNames_AreOnesExcelAccepts(string[] titles, string[] expected)
    {
        Assert.Equal(expected, ReportXlsxWriter.SheetNames(titles));
    }
}
