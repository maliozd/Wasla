using System.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Printing;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.Web;
using Wasla.Web.Reports.Export;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// Synthetic reports for the export tests (WAS-184), built by the shared report's own aggregation
/// (<see cref="OrderReportService.Build"/>) from known groups, so every expected figure is exact.
/// </summary>
internal static class ReportExportFixtures
{
    public static readonly IStringLocalizer Localizer = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }),
            NullLoggerFactory.Instance)
        .Create(typeof(SharedResource));

    /// <summary>The clock of every fixture: 10 November 2026, 11:10 UTC (14:10 in Türkiye).</summary>
    public static readonly DateTime Now = new(2026, 11, 10, 11, 10, 5, DateTimeKind.Utc);

    public const string Restaurant = "Kebapçı Ömer & Şükrü";

    public static readonly TimeZoneInfo Zone = TimeZoneHelper.ResolveTurkeyTimeZone();

    public static OrderReportFilter Filter(string start, string end, string? platform = null, string preset = "custom") =>
        OrderReportFilters.Resolve(preset, preset == "custom" ? start : null, preset == "custom" ? end : null, platform, Now, Zone).Filter
        ?? throw new InvalidOperationException("The fixture filter was refused.");

    /// <summary>
    /// 6 to 8 October 2026, every platform: 11 orders, 7 delivered for 2,645.05, all orders 2,907.55, one cancelled or
    /// rejected, one failed, one in progress, one in a retired status. 7 October has no orders.
    /// </summary>
    public static OrderReport Known(FoodPlatform? platform = null)
    {
        var filter = Filter("2026-10-06", "2026-10-08", platform?.ToString());
        var first = new DateOnly(2026, 10, 6);
        var third = new DateOnly(2026, 10, 8);
        var groups = new List<OrderReportService.Group>
        {
            new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, first, 3, 900.30m),
            new(FoodPlatform.TrendyolYemek, OrderStatus.Cancelled, first, 1, 120m),
            new(FoodPlatform.Yemeksepeti, OrderStatus.Delivered, first, 2, 410.20m),
            new(FoodPlatform.Yemeksepeti, OrderStatus.New, first, 1, 55.50m),
            new(FoodPlatform.GetirYemek, OrderStatus.Failed, first, 1, 77m),
            new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, third, 1, 99.99m),
            new(FoodPlatform.GetirYemek, OrderStatus.Delivered, third, 1, 1234.56m),
            new(FoodPlatform.Yemeksepeti, (OrderStatus)1, third, 1, 10m)
        };
        if (platform is { } only)
            groups = groups.Where(g => g.Platform == only).ToList();
        return OrderReportService.Build(filter, groups, Now);
    }

    /// <summary>A period without orders.</summary>
    public static OrderReport Empty() => OrderReportService.Build(Filter("2026-10-06", "2026-10-08"), [], Now);

    /// <summary>The longest period (92 days), ending today, with orders on most days: a multi-page PDF.</summary>
    public static OrderReport Long()
    {
        var end = OrderReportFilters.LocalToday(Now, Zone);
        var start = end.AddDays(1 - OrderReportFilters.MaxRangeDays);
        var filter = Filter(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var groups = new List<OrderReportService.Group>();
        for (var i = 0; i < OrderReportFilters.MaxRangeDays; i++)
        {
            if (i % 9 == 4)
                continue;
            var day = start.AddDays(i);
            groups.Add(new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, day, 10 + i % 7, 2_150.40m + i));
            groups.Add(new(FoodPlatform.Yemeksepeti, OrderStatus.Delivered, day, 6 + i % 5, 1_310.25m + i));
            groups.Add(new(FoodPlatform.GetirYemek, OrderStatus.Cancelled, day, 1 + i % 3, 95m));
            if (i == OrderReportFilters.MaxRangeDays - 1)
                groups.Add(new(FoodPlatform.GetirYemek, OrderStatus.Preparing, day, 2, 180m));
        }

        return OrderReportService.Build(filter, groups, Now);
    }

    public static ReportExportDocument Document(OrderReport report, string culture = "en-US", string restaurant = Restaurant)
    {
        using var _ = new CultureScope(culture);
        return new ReportExportBuilder(Localizer).Build(report, restaurant);
    }

    /// <summary>The CSV as text, without its byte order mark.</summary>
    public static string CsvText(byte[] csv)
    {
        Assert.True(csv.Length >= 3 && csv[0] == 0xEF && csv[1] == 0xBB && csv[2] == 0xBF, "The CSV must start with the UTF-8 byte order mark.");
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(csv, 3, csv.Length - 3);
    }

    /// <summary>An RFC 4180 reader for the tests: records of fields, quotes and doubled quotes undone.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                i++;
            }
            else
            {
                Assert.False(c == '\r' || c == '\n', "A line break outside quotes must be CRLF.");
                field.Append(c);
            }
        }

        Assert.False(quoted, "A quoted field was not closed.");
        Assert.True(field.Length == 0 && record.Count == 0, "The CSV must end with CRLF.");
        return records;
    }

    /// <summary>The records of one CSV section: the lines after its title line up to the next empty line.</summary>
    public static List<List<string>> Section(List<List<string>> records, string title)
    {
        var start = records.FindIndex(r => r.Count == 1 && r[0] == title);
        Assert.True(start >= 0, $"The CSV has no section titled {title}.");
        return records.Skip(start + 1).TakeWhile(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
    }

    public static string L(string key, string culture)
    {
        using var _ = new CultureScope(culture);
        return Localizer[key].Value;
    }
}

/// <summary>Sets the request culture of a Web request (Gregorian in every language) for the current flow.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string name)
    {
        var culture = GregorianCulture.For(new CultureInfo(name));
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
