using System.Globalization;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Web.Models.Reports;

namespace Wasla.Web.Reports.Export;

/// <summary>
/// Builds the export document of one report (WAS-184, docs/orders/reporting.md#export). It selects and labels the report's
/// summary, platform rows and day rows and the page's own charts; it calculates no figure. Labels and display values use
/// the current UI culture, as the report page does.
/// </summary>
public sealed class ReportExportBuilder(IStringLocalizer localizer)
{
    private readonly ReportPresenter _presenter = new(localizer);

    public ReportExportDocument Build(OrderReport report, string restaurantName)
    {
        ArgumentNullException.ThrowIfNull(report);

        var culture = CultureInfo.CurrentCulture;
        var period = report.Filter.Period;
        var generatedUtc = DateTime.SpecifyKind(report.GeneratedAtUtc, DateTimeKind.Utc);
        var generated = new DateTimeOffset(
            DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(generatedUtc, period.TimeZone), DateTimeKind.Unspecified),
            period.TimeZone.GetUtcOffset(generatedUtc));
        var currency = report.CurrencyCode;
        var platformLabel = report.Filter.Platform is { } platform ? L(ReportFormat.PlatformKey(platform)) : L("Reports.AllPlatforms");

        var details = new List<ReportExportDetail>
        {
            new(L("Reports.Export.Restaurant"), ReportExportCell.OfText(restaurantName)),
            new(L("Reports.Export.Selection"), ReportExportCell.OfText(L(PresetKey(period.Preset)))),
            new(L("Reports.Export.FirstDay"), ReportExportCell.OfDate(period.StartDate, ReportFormat.DayWithYear(period.StartDate))),
            new(L("Reports.Export.LastDay"), ReportExportCell.OfDate(period.EndDate, ReportFormat.DayWithYear(period.EndDate))),
            new(L("Reports.Export.DayCount"), ReportExportCell.OfCount(period.DayCount)),
            new(L("Reports.Export.PeriodStatus"), ReportExportCell.OfText(period.IsComplete ? L("Reports.CompletePeriod") : L("Reports.PartialPeriod"))),
            new(L("Reports.PlatformLabel"), ReportExportCell.OfText(platformLabel)),
            new(L("Reports.Export.Currency"), ReportExportCell.OfText(currency)),
            new(L("Reports.Export.TimeZone"), ReportExportCell.OfText(period.TimeZoneId)),
            new(L("Reports.Export.GeneratedAt"), ReportExportCell.OfMoment(
                generated,
                ReportFormat.DayWithYear(DateOnly.FromDateTime(generated.DateTime)) + " " + ReportFormat.Time(generated.DateTime)))
        };

        var measures = Measures(currency);
        var summary = new ReportExportTable(
            L("Reports.Export.SummarySheet"),
            [
                new(L("Reports.Metric"), ReportExportValueKind.Text),
                new(L("Reports.Export.Value"), ReportExportValueKind.Count),
                new(L("Reports.Export.Definition"), ReportExportValueKind.Text)
            ],
            measures.Select(m => (IReadOnlyList<ReportExportCell>)
                [ReportExportCell.OfText(m.SummaryLabel), m.Value(report.Summary), ReportExportCell.OfText(L(m.DefinitionKey))]).ToList());

        IReadOnlyList<ReportExportColumn> FigureColumns(ReportExportColumn first) =>
            [first, .. measures.Select(m => new ReportExportColumn(m.ColumnLabel, m.Kind))];

        var platforms = new ReportExportTable(
            L("Reports.Section.Platforms"),
            FigureColumns(new(L("Reports.Platform"), ReportExportValueKind.Text)),
            report.Platforms.Select(p => (IReadOnlyList<ReportExportCell>)
                [ReportExportCell.OfText(L(ReportFormat.PlatformKey(p.Platform))), .. measures.Select(m => m.Value(p.Figures))]).ToList());

        var days = new ReportExportTable(
            L("Reports.Export.DailySheet"),
            FigureColumns(new(L("Reports.Day"), ReportExportValueKind.Date)),
            report.Days.Select(d => (IReadOnlyList<ReportExportCell>)
                [ReportExportCell.OfDate(d.Date, ReportFormat.DayWithWeekday(d.Date)), .. measures.Select(m => m.Value(d.Figures))]).ToList());

        var notes = new List<string>();
        if (report.Summary.OrderCount == 0)
            notes.Add(L("Reports.NoOrdersTitle"));
        if (!period.IsComplete)
            notes.Add(L("Reports.Export.Note.Partial"));
        notes.Add(Format("Reports.Export.Note.Values", currency));
        notes.Add(Format("Reports.Export.Note.TimeBasis", period.TimeZoneId));
        notes.Add(L("Reports.Export.Note.CurrentStatus"));
        notes.Add(L("Reports.CancellationsCombinedNote"));
        notes.Add(L("Reports.Export.Note.NotDefined"));
        notes.Add(L("Reports.Export.Note.Practice"));

        return new ReportExportDocument
        {
            Report = report,
            Culture = culture,
            IsRightToLeft = culture.TextInfo.IsRightToLeft,
            Title = L("Reports.Export.DocumentTitle"),
            RestaurantName = ReportExportText.Clean(restaurantName),
            Details = details,
            Summary = summary,
            Platforms = platforms,
            Days = days,
            NotesTitle = L("Reports.Export.Notes"),
            Notes = notes,
            Print = BuildPrint(report, platformLabel)
        };
    }

    private ReportExportPrintContent BuildPrint(OrderReport report, string platformLabel)
    {
        var f = report.Summary;
        var period = report.Filter.Period;
        var headlines = new List<ReportExportHeadline>
        {
            new(L("Reports.TotalOrders"), ReportFormat.Count(f.OrderCount), null),
            new(L("Reports.CompletedValue"), ReportFormat.Money(f.CompletedOrderValue),
                f.CompletedOrderCount == 0 ? L("Reports.NoDeliveredOrders") : Format("Reports.DeliveredOrdersCount", ReportFormat.Count(f.CompletedOrderCount))),
            new(L("Reports.AverageCompletedValue"), ReportFormat.Money(f.AverageCompletedOrderValue),
                f.AverageCompletedOrderValue is null ? L("Reports.AverageNoDelivered") : null),
            new(L("Reports.AllOrderValue"), ReportFormat.Money(f.OrderValue), L("Reports.AllOrderValueHint"))
        };

        var isOneDay = report.Days.Count == 1;
        var legend = new List<ReportExportLegendItem>
        {
            new(ReportPresenter.SeriesDelivered, L("Reports.Outcome.Delivered")),
            new(ReportPresenter.SeriesOther, L("Reports.OtherStatuses"))
        };
        if (!period.IsComplete)
            legend.Add(new(ReportPresenter.SeriesPartial, L("Reports.TodayNotFinished")));

        var charts = isOneDay
            ? []
            : new List<ReportExportChart>
            {
                new(L("Reports.OrderCount"), _presenter.OrdersChart(report)),
                new(L("Reports.CompletedValue"), _presenter.ValueChart(report))
            };

        var shares = report.Filter.Platform is null
            ? _presenter.ShareRows(report)
                .Select(r => new ReportExportShare(
                    L(ReportFormat.PlatformKey(r.Platform)),
                    ReportPresenter.PlatformSeries(r.Platform),
                    ReportFormat.Count(r.Count),
                    ReportFormat.Percent(r.Share),
                    r.BarPercent))
                .ToList()
            : [];

        var notices = new List<string>();
        if (f.OrderCount == 0)
            notices.Add(L("Reports.NoOrdersTitle"));
        if (!period.IsComplete)
            notices.Add(L("Reports.Export.Note.Partial"));
        notices.Add(L("Reports.ValueDisclaimer"));

        return new ReportExportPrintContent(
            Subtitle: $"{ReportFormat.Range(period.StartDate, period.EndDate)} · {platformLabel}",
            PageNumberFormat: L("Reports.Export.PageNumber"),
            Headlines: headlines,
            DailyTitle: L("Reports.DailyTrend"),
            DailyHint: L("Reports.DailyTrendHint"),
            Legend: isOneDay ? [] : legend,
            Charts: charts,
            SingleDayNotice: isOneDay ? L("Reports.SingleDayNotice") : null,
            ShareTitle: shares.Count > 0 ? L("Reports.PlatformShare") : null,
            ShareHint: shares.Count > 0 ? L("Reports.PlatformShareHint") : null,
            Shares: shares,
            Notices: notices,
            NoRowsText: L("Reports.NoOrdersTitle"));
    }

    /// <summary>
    /// The figures every summary, platform row and day row exports, in this order: the status counts that add up to the
    /// order count, the cancellation or rejection rate, then the three order values. Each reads one field of
    /// <see cref="OrderReportFigures"/>.
    /// </summary>
    private IReadOnlyList<Measure> Measures(string currency)
    {
        string WithCurrency(string key) => string.Format(CultureInfo.CurrentCulture, L("Reports.Export.WithCurrency"), L(key), currency);

        return
        [
            new(L("Reports.TotalOrders"), L("Reports.OrderCount"), "Reports.Export.Definition.OrderCount", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.OrderCount)),
            new(L("Reports.Outcome.Delivered"), L("Reports.Outcome.Delivered"), "Reports.Export.Definition.Delivered", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.CompletedOrderCount)),
            new(L("Reports.Outcome.InProgress"), L("Reports.Outcome.InProgress"), "Reports.Export.Definition.InProgress", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.InProgressOrderCount)),
            new(L("Reports.Outcome.Cancelled"), L("Reports.Outcome.Cancelled"), "Reports.Export.Definition.Cancelled", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.CancelledOrderCount)),
            new(L("Reports.Outcome.Failed"), L("Reports.Outcome.Failed"), "Reports.Export.Definition.Failed", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.FailedOrderCount)),
            new(L("Reports.Outcome.Other"), L("Reports.Outcome.Other"), "Reports.Export.Definition.Other", ReportExportValueKind.Count,
                f => ReportExportCell.OfCount(f.OtherStatusOrderCount)),
            new(L("Reports.CancellationRate"), L("Reports.CancellationRate"), "Reports.Export.Definition.CancellationRate", ReportExportValueKind.Rate,
                f => ReportExportCell.OfRate(f.CancellationRate)),
            new(WithCurrency("Reports.CompletedValue"), WithCurrency("Reports.CompletedValue"), "Reports.Export.Definition.CompletedValue", ReportExportValueKind.Money,
                f => ReportExportCell.OfMoney(f.CompletedOrderValue)),
            new(WithCurrency("Reports.AverageCompletedValue"), WithCurrency("Reports.AverageCompletedValue"), "Reports.Export.Definition.AverageCompletedValue", ReportExportValueKind.Money,
                f => ReportExportCell.OfMoney(f.AverageCompletedOrderValue)),
            new(WithCurrency("Reports.AllOrderValue"), WithCurrency("Reports.AllOrderValue"), "Reports.Export.Definition.AllOrderValue", ReportExportValueKind.Money,
                f => ReportExportCell.OfMoney(f.OrderValue))
        ];
    }

    private sealed record Measure(string SummaryLabel, string ColumnLabel, string DefinitionKey, ReportExportValueKind Kind, Func<OrderReportFigures, ReportExportCell> Value);

    private static string PresetKey(OrderReportPreset preset) => preset switch
    {
        OrderReportPreset.Today => "Reports.Preset.Today",
        OrderReportPreset.Yesterday => "Reports.Preset.Yesterday",
        OrderReportPreset.Last7Days => "Reports.Preset.Last7Days",
        OrderReportPreset.Last30Days => "Reports.Preset.Last30Days",
        _ => "Reports.Preset.Custom"
    };

    private string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, L(key), args);

    private string L(string key) => localizer[key].Value;
}
