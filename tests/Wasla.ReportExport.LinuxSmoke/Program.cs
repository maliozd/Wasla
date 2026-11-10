using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Orders;
using Wasla.Application.Printing;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Web;
using Wasla.Web.Reports.Export;

// Report export smoke check on Linux (WAS-184). Run from a copy of the published linux-x64 Wasla.Web output:
//   dotnet exec --depsfile Wasla.Web.deps.json --runtimeconfig Wasla.Web.runtimeconfig.json Wasla.ReportExport.LinuxSmoke.dll <output>
// It exports synthetic English and Arabic reports (a short one and a 92-day multipage one) with the real exporter, renders
// every PDF page to PNG, and fails when the release's assemblies, native library, fonts, cultures or time zone are not what
// the export needs. Nothing here touches a database, a provider or the network.

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "report-export-linux");
Directory.CreateDirectory(output);
var manifest = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    manifest.AppendLine(line);
}

try
{
    var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    Log($"os: {RuntimeInformation.OSDescription}");
    Log($"runtime: {RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier} {RuntimeInformation.ProcessArchitecture}");
    Log($"base directory: {baseDirectory}");

    // The exporter and QuestPDF must come from the published output, and its native library must be there.
    foreach (var assembly in new[] { typeof(ReportPdfWriter).Assembly, typeof(QuestPDF.Settings).Assembly })
    {
        var directory = Path.GetDirectoryName(assembly.Location)!.TrimEnd(Path.DirectorySeparatorChar);
        Log($"assembly: {assembly.GetName().Name} {assembly.GetName().Version} from {directory}");
        Check(string.Equals(directory, baseDirectory, StringComparison.Ordinal), $"{assembly.GetName().Name} was not loaded from the published output.");
    }

    var native = Path.Combine(baseDirectory, "libQuestPdfSkia.so");
    Check(File.Exists(native), "libQuestPdfSkia.so is not in the published output.");
    Log($"native: {native} ({new FileInfo(native).Length} bytes)");

    // ICU and tzdata: Arabic month names and the Türkiye zone, as on the server.
    var arabic = GregorianCulture.For(new CultureInfo("ar-SA"));
    var october = arabic.DateTimeFormat.GetMonthName(10);
    Log($"ar-SA October: {october}, right to left: {arabic.TextInfo.IsRightToLeft}");
    Check(arabic.TextInfo.IsRightToLeft && october.Any(c => c is >= '؀' and <= 'ۿ'), "Arabic culture data (ICU) is missing.");
    var zone = TimeZoneHelper.ResolveTurkeyTimeZone();
    Log($"time zone: {zone.Id}");

    ReportPdfWriter.EnsureConfigured();
    // Every label and figure must have a glyph in the bundled fonts; a missing one fails here instead of drawing a box.
    QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;

    var localizer = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
            NullLoggerFactory.Instance)
        .Create(typeof(SharedResource));
    var now = new DateTime(2026, 11, 10, 11, 10, 5, DateTimeKind.Utc);

    var cases = new (string Name, string Culture, string Restaurant, OrderReport Report, int MinimumPages, ReportExportFormat[] Formats)[]
    {
        ("known-3-days_en-US", "en-US", "Corner Grill — Kebapçı Ömer", Known(now, zone), 3, [.. ReportExportFormats.All]),
        ("known-3-days_ar-SA", "ar-SA", "مطعم الشام — دمشق", Known(now, zone), 3, [.. ReportExportFormats.All]),
        ("92-days-partial_en-US", "en-US", "Corner Grill — Kebapçı Ömer", Long(now, zone), 5, [ReportExportFormat.Pdf, ReportExportFormat.Csv]),
        ("92-days-partial_ar-SA", "ar-SA", "مطعم الشام — دمشق", Long(now, zone), 5, [ReportExportFormat.Pdf])
    };

    foreach (var (name, culture, restaurant, report, minimumPages, formats) in cases)
    {
        var requestCulture = GregorianCulture.For(new CultureInfo(culture));
        CultureInfo.CurrentCulture = requestCulture;
        CultureInfo.CurrentUICulture = requestCulture;
        var document = new ReportExportBuilder(localizer).Build(report, restaurant);

        foreach (var format in formats)
        {
            var watch = Stopwatch.StartNew();
            var bytes = format switch
            {
                ReportExportFormat.Csv => ReportCsvWriter.Write(document),
                ReportExportFormat.Pdf => ReportPdfWriter.Write(document),
                _ => ReportXlsxWriter.Write(document)
            };
            watch.Stop();
            var file = $"{name}.{ReportExportFormats.Value(format)}";
            File.WriteAllBytes(Path.Combine(output, file), bytes);
            Check(format switch
            {
                ReportExportFormat.Csv => bytes is [0xEF, 0xBB, 0xBF, ..],
                ReportExportFormat.Pdf => bytes is [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', ..],
                _ => bytes is [(byte)'P', (byte)'K', 3, 4, ..]
            }, $"{file} does not start like a {format} file.");
            Log($"file: {file} {bytes.Length} bytes in {watch.ElapsedMilliseconds} ms");
        }

        if (formats.Contains(ReportExportFormat.Pdf))
        {
            var pages = 0;
            foreach (var image in ReportPdfWriter.Compose(document).GenerateImages(new ImageGenerationSettings { RasterDpi = 80, ImageFormat = ImageFormat.Png }))
                File.WriteAllBytes(Path.Combine(output, $"{name}.page-{++pages}.png"), image);
            Log($"pages: {name}.pdf {pages}");
            Check(pages >= minimumPages, $"{name}.pdf has {pages} pages; expected at least {minimumPages}.");
        }
    }

    Log("result: passed");
    return 0;
}
catch (Exception ex)
{
    Log($"result: failed: {ex.GetType().FullName}: {ex.Message}");
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    File.WriteAllText(Path.Combine(output, "manifest.txt"), manifest.ToString());
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

// The same synthetic cohorts as the unit tests (ReportExportFixtures), aggregated here with the public figures API: Known
// is 6–8 October 2026 with 11 orders, 7 delivered for 2,645.05 and all orders 2,907.55; Long is the longest period
// (92 days) ending today.
static OrderReport Known(DateTime now, TimeZoneInfo zone)
{
    var first = new DateOnly(2026, 10, 6);
    var third = new DateOnly(2026, 10, 8);
    return Build(Filter("2026-10-06", "2026-10-08", now, zone), now,
    [
        (FoodPlatform.TrendyolYemek, OrderStatus.Delivered, first, 3, 900.30m),
        (FoodPlatform.TrendyolYemek, OrderStatus.Cancelled, first, 1, 120m),
        (FoodPlatform.Yemeksepeti, OrderStatus.Delivered, first, 2, 410.20m),
        (FoodPlatform.Yemeksepeti, OrderStatus.New, first, 1, 55.50m),
        (FoodPlatform.GetirYemek, OrderStatus.Failed, first, 1, 77m),
        (FoodPlatform.TrendyolYemek, OrderStatus.Delivered, third, 1, 99.99m),
        (FoodPlatform.GetirYemek, OrderStatus.Delivered, third, 1, 1234.56m),
        (FoodPlatform.Yemeksepeti, (OrderStatus)1, third, 1, 10m)
    ]);
}

static OrderReport Long(DateTime now, TimeZoneInfo zone)
{
    var end = OrderReportFilters.LocalToday(now, zone);
    var start = end.AddDays(1 - OrderReportFilters.MaxRangeDays);
    var groups = new List<(FoodPlatform, OrderStatus, DateOnly, int, decimal)>();
    for (var i = 0; i < OrderReportFilters.MaxRangeDays; i++)
    {
        if (i % 9 == 4)
            continue;
        var day = start.AddDays(i);
        groups.Add((FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day, 10 + i % 7, 2_150.40m + i));
        groups.Add((FoodPlatform.Yemeksepeti, OrderStatus.Delivered, day, 6 + i % 5, 1_310.25m + i));
        groups.Add((FoodPlatform.GetirYemek, OrderStatus.Cancelled, day, 1 + i % 3, 95m));
    }

    return Build(Filter(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), now, zone), now, groups);
}

static OrderReportFilter Filter(string start, string end, DateTime now, TimeZoneInfo zone) =>
    OrderReportFilters.Resolve("custom", start, end, null, now, zone).Filter
    ?? throw new InvalidOperationException("The synthetic filter was refused.");

static OrderReport Build(OrderReportFilter filter, DateTime now, IReadOnlyList<(FoodPlatform Platform, OrderStatus Status, DateOnly Day, int Count, decimal Value)> groups)
{
    var period = filter.Period;
    var days = Enumerable.Range(0, period.DayCount).Select(i => period.StartDate.AddDays(i)).ToList();
    OrderReportFigures Sum(IEnumerable<(FoodPlatform Platform, OrderStatus Status, DateOnly Day, int Count, decimal Value)> rows) =>
        rows.Aggregate(OrderReportFigures.Empty, (total, g) => total + OrderReportFigures.ForStatus(g.Status, g.Count, g.Value));
    List<OrderReportDayRow> DayRows(IEnumerable<(FoodPlatform Platform, OrderStatus Status, DateOnly Day, int Count, decimal Value)> rows) =>
        days.Select((day, i) => new OrderReportDayRow(day, period.DayStartsUtc[i], period.DayStartsUtc[i + 1], Sum(rows.Where(g => g.Day == day)))).ToList();

    var platforms = groups.GroupBy(g => g.Platform)
        .Select(g => new OrderReportPlatformRow(g.Key, Sum(g)))
        .OrderByDescending(p => p.Figures.OrderCount).ThenBy(p => p.Platform)
        .ToList();
    return new OrderReport(
        filter,
        OrderCurrency.Code,
        now,
        Sum(groups),
        platforms,
        DayRows(groups),
        platforms.Select(p => new OrderReportPlatformDays(p.Platform, DayRows(groups.Where(g => g.Platform == p.Platform)))).ToList(),
        []);
}
