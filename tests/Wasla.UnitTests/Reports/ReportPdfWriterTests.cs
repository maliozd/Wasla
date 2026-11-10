using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// QuestPDF settings are process-wide; the tests that render PDFs run one at a time so a test that tightens them (the glyph
/// check) cannot affect another rendering.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ReportPdfCollection
{
    public const string Name = "Report PDF rendering";
}

/// <summary>
/// The PDF export (WAS-184), read back with PdfPig: its text (so it is searchable), figures, page numbers, repeated table
/// headers, Arabic right-to-left layout, glyph coverage in every language and the absence of active content.
/// </summary>
[Collection(ReportPdfCollection.Name)]
public sealed class ReportPdfWriterTests
{
    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static IReadOnlyList<string> PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p => Squash(p.Text)).ToList();
    }

    [Fact]
    public void Pdf_HoldsTheReportsFiguresAndLabels_AsText()
    {
        var pdf = ReportPdfWriter.Write(Document(Known()));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        var pages = PageTexts(pdf);
        var all = string.Concat(pages);
        foreach (var expected in new[]
                 {
                     "Orders report", Restaurant, "6 Oct - 8 Oct 2026", "Europe/Istanbul", "TRY",
                     "Total orders", "₺2,645.05", "₺377.86", "₺2,907.55", "9.1%",
                     "Trendyol GO", "₺1,120.29", "GetirYemek", "₺1,234.56",
                     "6 Oct, Tue", "Cancelled or rejected", "Completed order value (TRY)",
                     "not payments received, settled revenue, profit or payouts"
                 })
        {
            Assert.Contains(Squash(expected), all, StringComparison.Ordinal);
        }

        for (var i = 0; i < pages.Count; i++)
            Assert.Contains($"Page{i + 1}of{pages.Count}", pages[i], StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_Of92Days_RunsOverSeveralPages_AndRepeatsTheTableHeaderOnEach()
    {
        var document = Document(Long());
        var pages = PageTexts(ReportPdfWriter.Write(document));

        Assert.True(pages.Count >= 3, $"Expected several pages, got {pages.Count}.");
        for (var i = 0; i < pages.Count; i++)
            Assert.Contains($"Page{i + 1}of{pages.Count}", pages[i], StringComparison.Ordinal);

        // Every page that holds day rows carries the daily table's header row; the first and last day are both there.
        var dayRows = document.Days.Rows.Select(r => Squash(r[0].Display)).ToList();
        var header = Squash(string.Concat(document.Days.Columns.Select(c => c.Header)));
        var pagesWithDays = pages.Where(p => dayRows.Any(d => p.Contains(d, StringComparison.Ordinal))).ToList();
        Assert.True(pagesWithDays.Count >= 2, "The daily table should continue on another page.");
        Assert.All(pagesWithDays, p => Assert.Contains(header, p, StringComparison.Ordinal));
        Assert.Contains(pages, p => p.Contains(dayRows[0], StringComparison.Ordinal));
        Assert.Contains(pages, p => p.Contains(dayRows[^1], StringComparison.Ordinal));
        Assert.Contains(Squash("The period includes today, which is not over"), string.Concat(pages), StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_InArabic_IsLaidOutRightToLeft_WithShapedArabicGlyphs()
    {
        var pdf = ReportPdfWriter.Write(Document(Known(), "ar-SA", "مطعم الشام"));

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        var arabic = page.Letters.Where(l => l.Value.Any(IsArabic)).ToList();
        Assert.NotEmpty(arabic);
        Assert.All(arabic, l => Assert.Contains("NotoSansArabic", l.FontName, StringComparison.OrdinalIgnoreCase));

        // The title starts the header, so in Arabic it stands at the right edge of the page.
        var top = arabic.Max(l => l.StartBaseLine.Y);
        var titleLine = arabic.Where(l => Math.Abs(l.StartBaseLine.Y - top) < 1).ToList();
        Assert.True(titleLine.Min(l => l.BoundingBox.Left) > page.Width / 2, "The Arabic title should be on the right half of the page.");

        // Shaped glyphs are written: contextual (presentation) forms, not isolated letters left to right.
        Assert.Contains(arabic, l => l.Value.Any(c => c is >= 'ﹰ' and <= '﻿'));

        // What text extraction recovers on an Arabic page: every figure and Latin value, and Arabic words drawn from
        // single-glyph letters (مطعم, in visual order). Noto Sans Arabic draws dotted letters (ب ت ش ي ...) as a dotless form
        // plus separate dots, which the PDF's character map cannot name, so a search for a word with dotted letters can miss.
        // That limit is documented in docs/orders/reporting.md#pdf.
        var text = Squash(page.Text);
        foreach (var value in new[] { "Europe/Istanbul", "TRY", "2026" })
            Assert.Contains(value, text, StringComparison.Ordinal);
        var extracted = Squash(string.Concat(page.Letters.Select(l => l.Value)).Normalize(NormalizationForm.FormKC));
        Assert.True(extracted.Contains("مطعم", StringComparison.Ordinal) || extracted.Contains("معطم", StringComparison.Ordinal), "Undotted Arabic text should be extractable.");
    }

    [Theory]
    [InlineData("tr-TR", "Kebapçı Ömer & Şükrü — İğdır")]
    [InlineData("en-US", "Corner Grill")]
    [InlineData("ar-SA", "مطعم الشام — دمشق")]
    [InlineData("ru-RU", "Шашлычная «Ёлка»")]
    public void EveryLanguage_HasGlyphsForEveryLabelAndFigure_InTheBundledFonts(string culture, string restaurant)
    {
        ReportPdfWriter.EnsureConfigured();
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        try
        {
            foreach (var report in new[] { Known(), Long(), Empty() })
                Assert.NotEmpty(ReportPdfWriter.Write(Document(report, culture, restaurant)));
        }
        finally
        {
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        }
    }

    [Fact]
    public void LongUnbrokenLabels_AreWrapped_NotClipped()
    {
        var name = string.Concat(Enumerable.Repeat("Çok-uzun-restoran-adı_", 14)) + "🍕";
        var pages = PageTexts(ReportPdfWriter.Write(Document(Long(), "tr-TR", name)));

        Assert.Contains(Squash(name.Replace("🍕", "", StringComparison.Ordinal)), pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReportText_NeverBecomesActiveContent()
    {
        const string name = "<script>app.alert(1)</script><a href=\"http://evil.test/x.png\">x</a> javascript:void(0)";
        var pdf = ReportPdfWriter.Write(Document(Known(), restaurant: name));

        using var document = PdfDocument.Open(pdf);
        Assert.Contains(Squash(name), Squash(document.GetPage(1).Text), StringComparison.Ordinal);
        var catalog = document.Structure.Catalog.CatalogDictionary;
        foreach (var key in new[] { "OpenAction", "AA", "AcroForm", "Names" })
            Assert.False(catalog.ContainsKey(NameToken.Create(key)), $"The catalog has /{key}.");
        foreach (var page in document.GetPages())
        {
            Assert.Empty(page.GetAnnotations());
            Assert.Empty(page.GetHyperlinks());
            Assert.Equal(0, page.NumberOfImages);
            Assert.False(page.Dictionary.ContainsKey(NameToken.Create("AA")));
        }
    }

    [Fact]
    public void Pdf_OfAnEmptyPeriod_SaysSo()
    {
        var all = string.Concat(PageTexts(ReportPdfWriter.Write(Document(Empty()))));

        Assert.Contains(Squash("No orders in this period"), all, StringComparison.Ordinal);
        Assert.Contains(Squash("₺0.00"), all, StringComparison.Ordinal);
    }

    private static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';
}
