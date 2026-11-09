using System.Globalization;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Reports;

/// <summary>
/// Turns an <see cref="OrderReport"/> into what the report views draw. It selects and scales figures for display and adds
/// or recalculates none: every number is a field of the report, a share of two of its fields, or a difference between two
/// reports.
/// </summary>
public sealed class ReportPresenter(IStringLocalizer localizer)
{
    private const int ChartZeroLabelMaxDays = 31;

    public const string SeriesDelivered = "delivered";
    public const string SeriesOther = "other";
    public const string SeriesPartial = "partial";
    public const string SeriesCompletedValue = "value";
    public const string SeriesCancelled = "cancelled";
    public const string SeriesKnownRejected = "known-rejected";
    public const string SeriesKnownCancelled = "known-cancelled";
    public const string SeriesUnknownEnding = "unknown";

    public static string PlatformSeries(FoodPlatform platform) => platform switch
    {
        FoodPlatform.Yemeksepeti => "ys",
        FoodPlatform.GetirYemek => "gy",
        _ => "ty"
    };

    public static int OtherCount(OrderReportFigures f) => f.OrderCount - f.CompletedOrderCount;

    /// <summary>The share of <paramref name="part"/> in <paramref name="whole"/>, from 0 to 1, or null without a denominator.</summary>
    public static decimal? Share(decimal part, decimal whole) => whole == 0m ? null : part / whole;

    public ReportBarChart OrdersChart(OrderReport report)
    {
        var max = report.Days.Select(d => d.Figures.OrderCount).DefaultIfEmpty(0).Max();
        var axis = NiceMax(max);
        return Bars(report, report.Days, L("Reports.OrdersPerDay"), ReportFormat.Count(axis), ReportFormat.Count(axis / 2), ReportFormat.Count(0), day =>
        {
            var f = day.Figures;
            return IsPartialDay(report, day)
                ? [new ReportChartSegment(SeriesPartial, Height(f.OrderCount, axis))]
                : [new ReportChartSegment(SeriesDelivered, Height(f.CompletedOrderCount, axis)), new ReportChartSegment(SeriesOther, Height(OtherCount(f), axis))];
        }, f => f.OrderCount == 0, DayTip);
    }

    public ReportBarChart ValueChart(OrderReport report)
    {
        var max = report.Days.Select(d => d.Figures.CompletedOrderValue).DefaultIfEmpty(0m).Max();
        var axis = NiceMax(max);
        return Bars(report, report.Days, L("Reports.CompletedValuePerDay"), ReportFormat.WholeMoney(axis), ReportFormat.WholeMoney(axis / 2m), ReportFormat.WholeMoney(0m), day =>
            [new ReportChartSegment(IsPartialDay(report, day) ? SeriesPartial : SeriesCompletedValue, Height(day.Figures.CompletedOrderValue, axis))],
            f => f.CompletedOrderValue == 0m, DayTip);
    }

    public ReportBarChart CancellationsChart(OrderReport report)
    {
        var max = report.Days.Select(d => d.Figures.CancelledOrderCount).DefaultIfEmpty(0).Max();
        var axis = NiceMax(max);
        return Bars(report, report.Days, L("Reports.CancellationsPerDay"), ReportFormat.Count(axis), ReportFormat.Count(axis / 2), ReportFormat.Count(0), day =>
            [new ReportChartSegment(SeriesCancelled, Height(day.Figures.CancelledOrderCount, axis))],
            f => f.CancelledOrderCount == 0, CancellationTip);
    }

    /// <summary>One platform's daily orders; the charts of all platforms share <paramref name="axis"/> so they compare.</summary>
    public ReportBarChart PlatformChart(OrderReport report, OrderReportPlatformDays platform, decimal axis) =>
        Bars(report, platform.Days, L("Reports.OrdersPerDay"), ReportFormat.Count(axis), ReportFormat.Count(axis / 2), ReportFormat.Count(0), day =>
            [new ReportChartSegment(PlatformSeries(platform.Platform), Height(day.Figures.OrderCount, axis))],
            f => f.OrderCount == 0, DayTip);

    public static decimal SharedAxis(OrderReport report) =>
        NiceMax(report.PlatformDays.SelectMany(p => p.Days).Select(d => d.Figures.OrderCount).DefaultIfEmpty(0).Max());

    public IReadOnlyList<ReportShareRow> ShareRows(OrderReport report)
    {
        var largest = report.Platforms.Select(p => p.Figures.OrderCount).DefaultIfEmpty(0).Max();
        return report.Platforms
            .Select(p => new ReportShareRow(
                p.Platform,
                p.Figures.OrderCount,
                largest == 0 ? 0 : Height(p.Figures.OrderCount, largest),
                Share(p.Figures.OrderCount, report.Summary.OrderCount) ?? 0m))
            .ToList();
    }

    public IReadOnlyList<ReportStripSegment> OrderShareStrip(OrderReport report) => report.Platforms
        .Where(p => p.Figures.OrderCount > 0)
        .Select(p => new ReportStripSegment(p.Platform, p.Figures.OrderCount, Share(p.Figures.OrderCount, report.Summary.OrderCount) ?? 0m, ReportFormat.Count(p.Figures.OrderCount)))
        .ToList();

    public IReadOnlyList<ReportStripSegment> ValueShareStrip(OrderReport report) => report.Platforms
        .Where(p => p.Figures.CompletedOrderValue > 0m)
        .Select(p => new ReportStripSegment(p.Platform, (double)p.Figures.CompletedOrderValue, Share(p.Figures.CompletedOrderValue, report.Summary.CompletedOrderValue) ?? 0m, ReportFormat.Money(p.Figures.CompletedOrderValue)))
        .ToList();

    /// <summary>
    /// The three groups every cancelled or rejected order falls into, as shares of <see cref="OrderReportFigures.CancelledOrderCount"/>.
    /// Which origin belongs to which group is decided by <see cref="OrderReportStatuses.ClassifyCancellation"/>.
    /// </summary>
    public static IReadOnlyList<ReportEndingRow> EndingRows(OrderReportFigures f) =>
    [
        new(SeriesKnownRejected, "Reports.Ending.KnownRejected", "Reports.Ending.KnownRejectedHint", f.KnownRejectedOrderCount, Share(f.KnownRejectedOrderCount, f.CancelledOrderCount)),
        new(SeriesKnownCancelled, "Reports.Ending.KnownCancelled", "Reports.Ending.KnownCancelledHint", f.KnownCancelledOrderCount, Share(f.KnownCancelledOrderCount, f.CancelledOrderCount)),
        new(SeriesUnknownEnding, "Reports.Ending.Unknown", "Reports.Ending.UnknownHint", f.UnknownOutcomeOrderCount, Share(f.UnknownOutcomeOrderCount, f.CancelledOrderCount))
    ];

    /// <summary>
    /// Each platform with cancelled or rejected orders, in report order, with its own reasons. Every share is of that
    /// platform's cancelled or rejected orders, so the orders without a reason are part of the denominator and the
    /// platform's rows add up to all of them. Bars are scaled to the group's largest row.
    /// </summary>
    public IReadOnlyList<ReportReasonGroup> ReasonGroups(OrderReport report) => report.Platforms
        .Where(p => p.Figures.CancelledOrderCount > 0)
        .Select(p =>
        {
            var f = p.Figures;
            var rows = report.CancellationReasons.Where(r => r.Platform == p.Platform).ToList();
            var largest = rows.Select(r => r.OrderCount).Append(f.CancellationReasonMissingOrderCount).Max();
            return new ReportReasonGroup(
                p.Platform,
                f.CancelledOrderCount,
                f.CancellationReasonRecordedOrderCount,
                f.CancellationReasonCoverage,
                f.CancellationReasonMissingOrderCount,
                Share(f.CancellationReasonMissingOrderCount, f.CancelledOrderCount),
                Height(f.CancellationReasonMissingOrderCount, largest),
                ProviderCancellationReasons.For(p.Platform).Count > 0 || OrderRejectReasons.For(p.Platform).Count > 0,
                rows.Select(r => new ReportReasonRow(ReasonLabel(r.Platform, r.ReasonCode), r.ReasonCode, r.OrderCount, Share(r.OrderCount, f.CancelledOrderCount), Height(r.OrderCount, largest))).ToList());
        })
        .ToList();

    private string ReasonLabel(FoodPlatform platform, int code)
    {
        var label = localizer[ProviderCancellationReasons.LabelKey(platform, code)];
        return label.ResourceNotFound
            ? string.Format(CultureInfo.CurrentCulture, L("Reports.CancellationReason.Unlabelled"), ReportFormat.Code(code))
            : label.Value;
    }

    public static bool IsPartialDay(OrderReport report, OrderReportDayRow day) =>
        !report.Filter.Period.IsComplete && day.Date == report.Filter.Period.EndDate;

    public IReadOnlyList<ReportComparisonRow> ComparisonRows(OrderReportFigures current, OrderReportFigures previous)
    {
        ReportComparisonRow Count(string key, int a, int b) =>
            new(key, ReportFormat.Count(a), ReportFormat.Count(b), ReportFormat.SignedCount(a - b), Share(a - b, b));

        ReportComparisonRow Money(string key, decimal a, decimal b) =>
            new(key, ReportFormat.Money(a), ReportFormat.Money(b), ReportFormat.SignedMoney(a - b), Share(a - b, b));

        var average = current.AverageCompletedOrderValue is { } a && previous.AverageCompletedOrderValue is { } b
            ? Money("Reports.AverageCompletedValue", a, b)
            : new ReportComparisonRow(
                "Reports.AverageCompletedValue",
                ReportFormat.Money(current.AverageCompletedOrderValue),
                ReportFormat.Money(previous.AverageCompletedOrderValue),
                null,
                null);

        return
        [
            Count("Reports.TotalOrders", current.OrderCount, previous.OrderCount),
            Count("Reports.Outcome.Delivered", current.CompletedOrderCount, previous.CompletedOrderCount),
            Count("Reports.Outcome.InProgress", current.InProgressOrderCount, previous.InProgressOrderCount),
            Count("Reports.Outcome.Cancelled", current.CancelledOrderCount, previous.CancelledOrderCount),
            Count("Reports.Outcome.Failed", current.FailedOrderCount, previous.FailedOrderCount),
            Money("Reports.CompletedValue", current.CompletedOrderValue, previous.CompletedOrderValue),
            average,
            Money("Reports.AllOrderValue", current.OrderValue, previous.OrderValue)
        ];
    }

    /// <summary>Both periods' daily orders on one axis, aligned by day index.</summary>
    public ReportLineChart ComparisonChart(OrderReport current, OrderReport previous)
    {
        const double width = 1000;
        const double height = 200;
        var axis = NiceMax(current.Days.Concat(previous.Days).Select(d => d.Figures.OrderCount).DefaultIfEmpty(0).Max());
        var count = current.Days.Count;

        string Path(OrderReport report)
        {
            var points = report.Days.Select((d, i) =>
            {
                var x = count == 1 ? width / 2 : i * width / (count - 1);
                var y = height - (Height(d.Figures.OrderCount, axis) * height / 100);
                return string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}");
            });
            return "M" + string.Join(" L", points);
        }

        var labels = new List<(string, double)>();
        var step = LabelStep(count);
        for (var i = 0; i < count; i += step)
            labels.Add((string.Format(CultureInfo.CurrentCulture, L("Reports.DayIndex"), i + 1), count == 1 ? 50 : i * 100d / (count - 1)));

        return new ReportLineChart(Path(current), Path(previous), Ticks(ReportFormat.Count(axis), ReportFormat.Count(axis / 2), ReportFormat.Count(0)), labels);
    }

    /// <summary>
    /// A round even axis maximum at or above <paramref name="max"/>, so the middle gridline is whole. The axis only scales
    /// bars and labels its gridlines: figures keep their exact values. Near the top of the decimal range, where no round
    /// axis fits, it is the smallest whole number at or above <paramref name="max"/>.
    /// </summary>
    public static decimal NiceMax(decimal max)
    {
        var top = Math.Ceiling(max);
        if (top <= 2m)
            return 2m;

        var power = 1m;
        while (power <= top / 10m)
            power *= 10m;

        foreach (var factor in new[] { 1m, 2m, 4m, 5m, 6m, 8m, 10m })
        {
            if (power > decimal.MaxValue / factor)
                break;

            var axis = factor * power;
            if (axis >= top && axis % 2m == 0m)
                return axis;
        }

        return top;
    }

    private ReportBarChart Bars(
        OrderReport report,
        IReadOnlyList<OrderReportDayRow> days,
        string name,
        string top,
        string middle,
        string bottom,
        Func<OrderReportDayRow, IReadOnlyList<ReportChartSegment>> segments,
        Func<OrderReportFigures, bool> isZero,
        Func<OrderReport, OrderReportDayRow, IReadOnlyList<ReportChartTipRow>> tip)
    {
        var count = days.Count;
        var step = LabelStep(count);
        var partial = !report.Filter.Period.IsComplete;
        var columns = days.Select((day, index) =>
        {
            var isLast = index == count - 1;
            var isPartial = partial && isLast;
            string? label = null;
            if (isPartial)
                label = L("Common.Today");
            else if (index % step == 0 && !(partial && count - 1 - index < step / 2 + 1))
                label = ReportFormat.Day(day.Date);

            var rows = tip(report, day);
            var title = ReportFormat.DayWithWeekday(day.Date);
            var spoken = title + ": " + string.Join(", ", rows.Select(r => $"{r.Label} {r.Value}"));
            if (isPartial)
                spoken += ". " + L("Reports.TodayNotFinished");
            return new ReportChartColumn(day.Date, segments(day), isPartial, isZero(day.Figures), label, isPartial, spoken, title, rows);
        }).ToList();

        return new ReportBarChart(name, columns, Ticks(top, middle, bottom), count <= ChartZeroLabelMaxDays);
    }

    private IReadOnlyList<ReportChartTipRow> DayTip(OrderReport report, OrderReportDayRow day) =>
    [
        new(L("Reports.OrderCount"), ReportFormat.Count(day.Figures.OrderCount)),
        new(L("Reports.Outcome.Delivered"), ReportFormat.Count(day.Figures.CompletedOrderCount)),
        new(L("Reports.OtherStatuses"), ReportFormat.Count(OtherCount(day.Figures))),
        new(L("Reports.CompletedValue"), ReportFormat.Money(day.Figures.CompletedOrderValue))
    ];

    private IReadOnlyList<ReportChartTipRow> CancellationTip(OrderReport report, OrderReportDayRow day) =>
    [
        new(L("Reports.Outcome.Cancelled"), ReportFormat.Count(day.Figures.CancelledOrderCount)),
        new(L("Reports.Ending.KnownRejected"), ReportFormat.Count(day.Figures.KnownRejectedOrderCount)),
        new(L("Reports.Ending.KnownCancelled"), ReportFormat.Count(day.Figures.KnownCancelledOrderCount)),
        new(L("Reports.Ending.Unknown"), ReportFormat.Count(day.Figures.UnknownOutcomeOrderCount)),
        new(L("Reports.OrderCount"), ReportFormat.Count(day.Figures.OrderCount)),
        new(L("Reports.CancellationRate"), ReportFormat.Percent(day.Figures.CancellationRate))
    ];

    private static IReadOnlyList<ReportChartTick> Ticks(string top, string middle, string bottom) =>
        [new(top, 100), new(middle, 50), new(bottom, 0)];

    private static int LabelStep(int days) => days switch
    {
        <= 10 => 1,
        <= 20 => 3,
        <= 45 => 5,
        <= 70 => 10,
        _ => 15
    };

    private static double Height(decimal value, decimal axis) => axis == 0m ? 0d : (double)(value / axis * 100m);

    private string L(string key) => localizer[key].Value;
}
