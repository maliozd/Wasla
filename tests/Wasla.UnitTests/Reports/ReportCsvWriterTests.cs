using System.Text;
using Wasla.Domain.Enums;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>The CSV export (WAS-184): its fixed format, exact figures, Unicode text and formula-safe text.</summary>
public sealed class ReportCsvWriterTests
{
    private static List<List<string>> Records(ReportExportDocument document) => ParseCsv(CsvText(ReportCsvWriter.Write(document)));

    [Fact]
    public void Csv_HoldsTheExactFigures_OfTheReport()
    {
        var records = Records(Document(Known()));

        var summary = Section(records, "Summary");
        Assert.Equal(["Measure", "Value", "Definition"], summary[0]);
        string Value(string measure) => summary.Single(r => r[0] == measure)[1];
        Assert.Equal("11", Value("Total orders"));
        Assert.Equal("7", Value("Delivered"));
        Assert.Equal("1", Value("In progress"));
        Assert.Equal("1", Value("Cancelled or rejected"));
        Assert.Equal("1", Value("Failed"));
        Assert.Equal("1", Value("Other"));
        Assert.Equal("0.0909", Value("Cancellation or rejection rate"));
        Assert.Equal("2645.05", Value("Completed order value (TRY)"));
        Assert.Equal("377.86", Value("Average completed order (TRY)"));
        Assert.Equal("2907.55", Value("All order value (TRY)"));

        var platforms = Section(records, "Platforms");
        Assert.Equal(
            ["Platform", "Orders", "Delivered", "In progress", "Cancelled or rejected", "Failed", "Other", "Cancellation or rejection rate",
             "Completed order value (TRY)", "Average completed order (TRY)", "All order value (TRY)"],
            platforms[0]);
        Assert.Equal(["Trendyol GO", "5", "4", "0", "1", "0", "0", "0.2000", "1000.29", "250.07", "1120.29"], platforms[1]);
        Assert.Equal(["Yemeksepeti", "4", "2", "1", "0", "0", "1", "0.0000", "410.20", "205.10", "475.70"], platforms[2]);
        Assert.Equal(["GetirYemek", "2", "1", "0", "0", "1", "0", "0.0000", "1234.56", "1234.56", "1311.56"], platforms[3]);

        // Every local day, oldest first; a day without orders has no rate and no average, never 0.
        var days = Section(records, "Daily");
        Assert.Equal("Day", days[0][0]);
        Assert.Equal(["2026-10-06", "8", "5", "1", "1", "1", "0", "0.1250", "1310.50", "262.10", "1563.00"], days[1]);
        Assert.Equal(["2026-10-07", "0", "0", "0", "0", "0", "0", "", "0.00", "", "0.00"], days[2]);
        Assert.Equal(["2026-10-08", "3", "2", "0", "0", "0", "1", "0.0000", "1334.55", "667.28", "1344.55"], days[3]);
    }

    [Fact]
    public void Csv_NamesTheRestaurantPeriodPlatformCurrencyZoneAndTime_AndItsDefinitions()
    {
        var records = Records(Document(Known(FoodPlatform.TrendyolYemek)));

        Assert.Equal(["Orders report"], records[0]);
        string Detail(string label) => records.Single(r => r.Count == 2 && r[0] == label)[1];
        Assert.Equal(Restaurant, Detail("Restaurant"));
        Assert.Equal("Custom range", Detail("Period selected"));
        Assert.Equal("2026-10-06", Detail("First day"));
        Assert.Equal("2026-10-08", Detail("Last day"));
        Assert.Equal("3", Detail("Days"));
        Assert.Equal("Complete period", Detail("Period status"));
        Assert.Equal("Trendyol GO", Detail("Platform"));
        Assert.Equal("TRY", Detail("Currency"));
        Assert.Equal("Europe/Istanbul", Detail("Time zone"));
        Assert.Equal("2026-11-10T14:10:05+03:00", Detail("Created"));

        var summary = Section(records, "Summary");
        Assert.All(summary.Skip(1), row => Assert.False(string.IsNullOrWhiteSpace(row[2]), $"{row[0]} has no definition."));
        var notes = Section(records, "Notes");
        Assert.Contains(notes, n => n[0].Contains("not payments received, settled revenue, profit or payouts", StringComparison.Ordinal));
        Assert.Contains(notes, n => n[0].Contains("Europe/Istanbul", StringComparison.Ordinal));
        Assert.Contains(notes, n => n[0] == "Cancelled and rejected orders are counted together.");
        Assert.DoesNotContain(notes, n => n[0].Contains("today", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Csv_OfAPeriodWithToday_SaysItIsPartial()
    {
        var notes = Section(Records(Document(Long())), "Notes");

        Assert.Contains(notes, n => n[0].StartsWith("The period includes today, which is not over", StringComparison.Ordinal));
    }

    [Fact]
    public void Csv_OfAnEmptyPeriod_SaysSo_WithZeroDaysAndNoPlatformRows()
    {
        var records = Records(Document(Empty()));

        Assert.Single(Section(records, "Platforms"));
        var days = Section(records, "Daily");
        Assert.Equal(4, days.Count);
        Assert.All(days.Skip(1), d => Assert.Equal(["0", "0", "0", "0", "0", "0", "", "0.00", "", "0.00"], d.Skip(1)));
        Assert.Equal("No orders in this period", Section(records, "Notes")[0][0]);
        Assert.Equal("", Section(records, "Summary").Single(r => r[0] == "Average completed order (TRY)")[1]);
    }

    [Theory]
    [InlineData("tr-TR", "Toplam sipariş", "Özet", "Günlük", "Kebapçı Ömer & Şükrü")]
    [InlineData("ar-SA", "إجمالي الطلبات", "الملخص", "يومي", "مطعم الشام — دمشق")]
    [InlineData("ru-RU", "Всего заказов", "Сводка", "По дням", "Шашлычная «Ёлка»")]
    public void Csv_KeepsTurkishArabicAndRussianText_AndInvariantValues(string culture, string totalLabel, string summaryTitle, string dailyTitle, string restaurant)
    {
        var records = Records(Document(Known(), culture, restaurant));

        Assert.Contains(records, r => r.Count == 2 && r[1] == restaurant);
        var summary = Section(records, summaryTitle);
        Assert.Equal("11", summary.Single(r => r[0] == totalLabel)[1]);
        // Separators and digits never follow the culture: dot decimals, no grouping, Gregorian ISO dates.
        Assert.Contains(summary, r => r[1] == "2645.05");
        Assert.Contains(summary, r => r[1] == "2907.55");
        Assert.Equal("2026-10-06", Section(records, dailyTitle)[1][0]);
        Assert.Contains(records, r => r.Count == 2 && r[1] == "2026-11-10T14:10:05+03:00");
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://example.test\",\"x\")")]
    [InlineData("+90 555 000 00 00")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    [InlineData("  =1+1")]
    [InlineData("＝1+1")]
    [InlineData("＋1")]
    [InlineData("－1")]
    [InlineData("＠A1")]
    public void FormulaLikeText_GetsAnApostrophe(string text)
    {
        Assert.Equal("'" + text, ReportCsvWriter.Neutralize(text));
    }

    [Theory]
    [InlineData("\t=1")]
    [InlineData("\r=1")]
    [InlineData("\n@A1")]
    public void TextStartingWithATabOrLineBreak_GetsAnApostrophe(string text)
    {
        Assert.StartsWith("'", ReportCsvWriter.Neutralize(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Kebap-Evi")]
    [InlineData("Ali's @ Home")]
    [InlineData("2645.05")]
    [InlineData("")]
    [InlineData("مطعم = بيت")]
    public void OrdinaryText_IsUnchanged(string text)
    {
        Assert.Equal(text, ReportCsvWriter.Neutralize(text));
    }

    [Fact]
    public void Numbers_AreNeverPrefixed_EvenWhenNegative()
    {
        Assert.Equal("-12.50", ReportCsvWriter.Value(ReportExportCell.OfMoney(-12.5m)));
        Assert.Equal("-3", ReportCsvWriter.Value(ReportExportCell.OfCount(-3)));
        Assert.Equal("", ReportCsvWriter.Value(ReportExportCell.OfMoney(null)));
        Assert.Equal("", ReportCsvWriter.Value(ReportExportCell.OfRate(null)));
    }

    [Fact]
    public void ARestaurantNameWithFormulaDelimitersQuotesAndLineBreaks_StaysOneInertField()
    {
        const string name = "=cmd|' /C calc'!A0, \"Best\"\r\nKebap";
        var csv = CsvText(ReportCsvWriter.Write(Document(Known(), restaurant: name)));

        // One line, quoted, the formula neutralised, quotes doubled, the line break turned into a space.
        Assert.Contains("Restaurant,\"'=cmd|' /C calc'!A0, \"\"Best\"\"  Kebap\"\r\n", csv, StringComparison.Ordinal);
        var detail = ParseCsv(csv).Single(r => r.Count == 2 && r[0] == "Restaurant");
        Assert.Equal("'=cmd|' /C calc'!A0, \"Best\"  Kebap", detail[1]);
    }

    [Fact]
    public void Csv_IsUtf8WithCrlfLines_AndEveryRecordIsWellFormed()
    {
        var bytes = ReportCsvWriter.Write(Document(Known(), "ar-SA", "مطعم \"الشام\", دمشق"));

        var text = CsvText(bytes);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(bytes.Length - 3, Encoding.UTF8.GetByteCount(text));
        Assert.Contains(ParseCsv(text), r => r.Count == 2 && r[1] == "مطعم \"الشام\", دمشق");
    }
}
