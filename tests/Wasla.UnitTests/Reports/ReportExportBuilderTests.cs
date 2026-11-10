using Wasla.Application.Abstractions.Reports;
using Wasla.Domain.Enums;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// The export document (WAS-184): every figure is a field of one report, the summary, platform rows and day rows reconcile,
/// and it says what the figures are and are not.
/// </summary>
public sealed class ReportExportBuilderTests
{
    private static decimal? Figure(ReportExportCell cell) => cell.Number;

    [Fact]
    public void SummaryPlatformAndDayRows_AreTheReportsFields_AndReconcile()
    {
        var report = Known();
        var document = Document(report);

        Assert.Equal(report.Platforms.Count, document.Platforms.Rows.Count);
        Assert.Equal(report.Days.Count, document.Days.Rows.Count);

        // Columns 1 to 10 are the same measures in both tables; counts and amounts add up to the summary.
        var summaryValues = document.Summary.Rows.Select(r => r[1]).ToList();
        foreach (var measure in new[] { 0, 1, 2, 3, 4, 5, 7, 9 })
        {
            var expected = Figure(summaryValues[measure]);
            Assert.Equal(expected, document.Platforms.Rows.Sum(r => Figure(r[measure + 1])));
            Assert.Equal(expected, document.Days.Rows.Sum(r => Figure(r[measure + 1])));
        }

        // The status counts add up to the order count in every row.
        foreach (var row in document.Platforms.Rows.Concat(document.Days.Rows).Select(r => r.Skip(1).ToList()).Append(summaryValues))
            Assert.Equal(Figure(row[0]), Figure(row[1]) + Figure(row[2]) + Figure(row[3]) + Figure(row[4]) + Figure(row[5]));

        Assert.Equal(report.Summary.OrderCount, Figure(summaryValues[0]));
        Assert.Equal(report.Summary.CompletedOrderValue, Figure(summaryValues[7]));
        Assert.Equal(report.Summary.AverageCompletedOrderValue, Figure(summaryValues[8]));
        Assert.Equal(report.Summary.OrderValue, Figure(summaryValues[9]));
        Assert.Equal(report.Summary.CancellationRate, Figure(summaryValues[6]));
    }

    [Fact]
    public void AveragesAndRates_AreTheReportsOwn_NotRecalculatedFromTheExport()
    {
        var report = Known();
        var document = Document(report);

        for (var i = 0; i < report.Platforms.Count; i++)
        {
            Assert.Equal(report.Platforms[i].Figures.AverageCompletedOrderValue, Figure(document.Platforms.Rows[i][9]));
            Assert.Equal(report.Platforms[i].Figures.CancellationRate, Figure(document.Platforms.Rows[i][7]));
        }

        var zeroDay = document.Days.Rows[1];
        Assert.False(zeroDay[7].HasValue);
        Assert.False(zeroDay[9].HasValue);
        Assert.Equal("—", zeroDay[9].Display);
    }

    [Fact]
    public void Document_IdentifiesTheExport_WithTheReportsGenerationTime_InItsZone()
    {
        var document = Document(Known(FoodPlatform.GetirYemek));

        var created = document.Details.Single(d => d.Label == "Created").Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 10, 5, TimeSpan.FromHours(3)), created.Moment);
        Assert.Equal("GetirYemek", document.Details.Single(d => d.Label == "Platform").Value.Text);
        Assert.Equal("TRY", document.Details.Single(d => d.Label == "Currency").Value.Text);
        Assert.Equal("Europe/Istanbul", document.Details.Single(d => d.Label == "Time zone").Value.Text);
        Assert.Equal(Restaurant, document.RestaurantName);
        Assert.Equal("6 Oct - 8 Oct 2026 · GetirYemek", document.Print.Subtitle);
    }

    [Fact]
    public void Labels_SayWhatTheValuesAre_AndNeverCallThemRevenueProfitOrPayouts()
    {
        var document = Document(Known());
        var headers = document.Platforms.Columns.Select(c => c.Header)
            .Concat(document.Summary.Rows.Select(r => r[0].Text!))
            .ToList();

        Assert.Contains("Cancelled or rejected", headers);
        Assert.Contains("Completed order value (TRY)", headers);
        Assert.Contains("All order value (TRY)", headers);
        foreach (var word in new[] { "revenue", "profit", "payout", "income", "earning", "payment" })
            Assert.DoesNotContain(headers, h => h.Contains(word, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(document.Notes, n => n.Contains("not payments received, settled revenue, profit or payouts", StringComparison.Ordinal));
    }

    [Fact]
    public void PartialAndEmptyPeriods_AreAnnounced()
    {
        var partial = Document(Long());
        Assert.Contains(partial.Notes, n => n.StartsWith("The period includes today", StringComparison.Ordinal));
        Assert.Contains(partial.Print.Notices, n => n.StartsWith("The period includes today", StringComparison.Ordinal));
        Assert.Equal("Includes today, the day is not over", partial.Details.Single(d => d.Label == "Period status").Value.Text);

        var empty = Document(Empty());
        Assert.Equal("No orders in this period", empty.Notes[0]);
        Assert.Equal("No orders in this period", empty.Print.Notices[0]);
        Assert.Empty(empty.Platforms.Rows);
        Assert.Empty(empty.Print.Shares);

        var complete = Document(Known());
        Assert.DoesNotContain(complete.Notes, n => n.StartsWith("The period includes today", StringComparison.Ordinal));
    }

    [Fact]
    public void Charts_AreThePagesOwn_AndAOneDayPeriodHasNone()
    {
        var document = Document(Long());
        Assert.Equal(2, document.Print.Charts.Count);
        Assert.Equal(92, document.Print.Charts[0].Chart.Columns.Count);
        Assert.True(document.Print.Charts[0].Chart.Columns[^1].IsPartialDay);
        Assert.Contains(document.Print.Legend, l => l.Label == "Today, the day is not over");

        var oneDay = Document(Wasla.Infrastructure.Services.OrderReportService.Build(Filter("2026-10-06", "2026-10-06"), [], Now));
        Assert.Empty(oneDay.Print.Charts);
        Assert.NotNull(oneDay.Print.SingleDayNotice);
    }

    [Fact]
    public void PdfDayLabels_SpanWholeColumns_CentredUnderTheirBar_ExceptAtTheEdges()
    {
        var shortChart = Document(Known()).Print.Charts[0].Chart;
        Assert.Equal(
            [new(1, "6 Oct", ReportPdfWriter.AxisLabelAlign.Center, false), new(1, "7 Oct", ReportPdfWriter.AxisLabelAlign.Center, false), new ReportPdfWriter.AxisLabelGroup(1, "8 Oct", ReportPdfWriter.AxisLabelAlign.Center, false)],
            ReportPdfWriter.AxisGroups(shortChart.Columns));

        var longChart = Document(Long()).Print.Charts[0].Chart;
        var groups = ReportPdfWriter.AxisGroups(longChart.Columns);
        Assert.Equal(92, groups.Sum(g => g.Columns));
        var labelled = groups.Where(g => g.Label is not null).ToList();
        Assert.Equal(longChart.Columns.Count(c => c.AxisLabel is not null), labelled.Count);
        Assert.Equal(ReportPdfWriter.AxisLabelAlign.Start, labelled[0].Align);
        Assert.All(labelled.Skip(1).SkipLast(1), g => Assert.Equal(ReportPdfWriter.AxisLabelAlign.Center, g.Align));
        Assert.All(labelled.Skip(1).SkipLast(1), g => Assert.True(g.Columns % 2 == 1, "A centred label owns as many columns on each side."));
        Assert.Equal(ReportPdfWriter.AxisLabelAlign.End, labelled[^1].Align);
        Assert.True(labelled[^1].IsToday);
    }

    [Theory]
    [InlineData("Kebap\r\nEvi\t", "Kebap  Evi")]
    [InlineData("\u0001Bad\u0007Name￿", "Bad Name�")]
    [InlineData("  ", "")]
    [InlineData("Ömer 🍕 مطعم", "Ömer 🍕 مطعم")]
    public void Text_IsCleaned_ToOneLineEveryFormatCanHold(string input, string expected)
    {
        Assert.Equal(expected, ReportExportText.Clean(input));
    }

    [Fact]
    public void UnpairedSurrogates_BecomeReplacementCharacters()
    {
        Assert.Equal("a�b", ReportExportText.Clean("a\uD800b"));
    }

    [Theory]
    [InlineData(null, ReportExportFormat.Csv, "wasla-orders-report_2026-10-06_2026-10-08.csv")]
    [InlineData(FoodPlatform.TrendyolYemek, ReportExportFormat.Pdf, "wasla-orders-report_2026-10-06_2026-10-08_trendyolyemek.pdf")]
    [InlineData(FoodPlatform.GetirYemek, ReportExportFormat.Xlsx, "wasla-orders-report_2026-10-06_2026-10-08_getiryemek.xlsx")]
    public void FileNames_AreAsciiFromTheAcceptedFilter(FoodPlatform? platform, ReportExportFormat format, string expected)
    {
        // The Arabic culture would write Arabic dates and digits; the file name never follows the culture.
        Assert.Equal(expected, ReportExportFormats.FileName(Document(Known(platform), "ar-SA", "=evil\"name/..\\"), format));
    }

    [Theory]
    [InlineData("csv", ReportExportFormat.Csv)]
    [InlineData("PDF", ReportExportFormat.Pdf)]
    [InlineData("Xlsx", ReportExportFormat.Xlsx)]
    public void Formats_AreParsedByName(string value, ReportExportFormat expected)
    {
        Assert.True(ReportExportFormats.TryParse(value, out var format));
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" csv")]
    [InlineData("xls")]
    [InlineData("docx")]
    [InlineData("1")]
    public void OtherFormats_AreRefused(string? value)
    {
        Assert.False(ReportExportFormats.TryParse(value, out _));
    }
}
