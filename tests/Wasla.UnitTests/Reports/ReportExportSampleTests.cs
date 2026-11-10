using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Wasla.Application.Abstractions.Reports;
using Wasla.Web.Reports.Export;
using static Wasla.UnitTests.Reports.ReportExportFixtures;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// Representative export files (WAS-184) of synthetic reports in every language, for people to open: on GitHub Actions they
/// are written to <c>artifacts/test-results/report-export-samples</c>, which the CI workflow already uploads with the test
/// results. Elsewhere they are created and checked but not kept.
/// </summary>
[Collection(ReportPdfCollection.Name)]
public sealed class ReportExportSampleTests
{
    [Fact]
    public void RepresentativeExports_AreCreatedInEveryLanguage()
    {
        var onCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var samples = new List<(string Name, byte[] Bytes)>();
        void Add(string name, OrderReport report, string culture, string restaurant, params ReportExportFormat[] formats)
        {
            var document = Document(report, culture, restaurant);
            foreach (var format in formats)
            {
                var bytes = format switch
                {
                    ReportExportFormat.Csv => ReportCsvWriter.Write(document),
                    ReportExportFormat.Pdf => ReportPdfWriter.Write(document),
                    _ => ReportXlsxWriter.Write(document)
                };
                Assert.NotEmpty(bytes);
                samples.Add(($"{name}.{ReportExportFormats.Value(format)}", bytes));
            }

            // Page images of each PDF, so a reviewer can look at the pages without a PDF viewer.
            if (onCi && formats.Contains(ReportExportFormat.Pdf))
            {
                var page = 0;
                foreach (var image in ReportPdfWriter.Compose(document).GenerateImages(new ImageGenerationSettings { RasterDpi = 80, ImageFormat = ImageFormat.Png }))
                    samples.Add(($"{name}.page-{++page}.png", image));
            }
        }

        var all = ReportExportFormats.All.ToArray();
        Add("known-3-days_tr-TR", Known(), "tr-TR", "Kebapçı Ömer & Şükrü — İğdır", all);
        Add("known-3-days_en-US", Known(), "en-US", "=HYPERLINK(\"http://example.test\",\"Corner Grill\")", all);
        Add("known-3-days_ar-SA", Known(), "ar-SA", "مطعم الشام — دمشق", all);
        Add("known-3-days_ru-RU", Known(), "ru-RU", "Шашлычная «Ёлка»", all);
        Add("92-days-partial_tr-TR", Long(), "tr-TR", string.Concat(Enumerable.Repeat("Çok-uzun-restoran-adı_", 14)), all);
        Add("92-days-partial_ar-SA", Long(), "ar-SA", "مطعم الشام — دمشق", ReportExportFormat.Pdf);
        Add("empty_en-US", Empty(), "en-US", "Corner Grill", ReportExportFormat.Pdf, ReportExportFormat.Csv);

        if (!onCi)
            return;

        var directory = Path.Combine(RepositoryRoot(), "artifacts", "test-results", "report-export-samples");
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in samples)
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        TestContext.Current.SendDiagnosticMessage($"Wrote {samples.Count} report export samples to {directory}.");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Wasla.sln was not found above the test output.");
    }
}
