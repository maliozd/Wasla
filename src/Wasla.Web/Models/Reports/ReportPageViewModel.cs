using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Reports;

public enum ReportResultState
{
    Report,
    NoOrders,
    InvalidFilter,
    Failed
}

public enum ReportComparisonState
{
    Off,
    Active,

    /// <summary>The period includes today, so nothing equivalent exists to compare with.</summary>
    NeedsCompletePeriod,

    /// <summary>The preceding period is outside the accepted dates.</summary>
    Unavailable
}

/// <summary>The report page and its refreshable result. Every figure comes from <see cref="IOrderReportService"/>.</summary>
public sealed class ReportPageViewModel
{
    public required ReportPageQuery Query { get; init; }

    public required ReportResultState State { get; init; }

    /// <summary>The local day the page was built for, for the date inputs' upper bound and invariant values.</summary>
    public required DateOnly Today { get; init; }

    public OrderReport? Report { get; init; }

    public ReportComparisonState Comparison { get; init; } = ReportComparisonState.Off;

    public OrderReport? Previous { get; init; }

    /// <summary>The report of each platform in the cohort, for the daily chart per platform; empty unless that section shows.</summary>
    public IReadOnlyList<OrderReport> PlatformReports { get; init; } = [];

    public IReadOnlyList<ReportFilterProblem> Problems { get; init; } = [];

    public DateTime GeneratedAtLocal { get; init; }

    public OrderReportFilter? Filter => Report?.Filter;

    public string? StartDateError => Problems.FirstOrDefault(p => p.Parameter == OrderReportFilters.StartDateParameter)?.ResourceKey;

    public string? EndDateError => Problems.FirstOrDefault(p => p.Parameter == OrderReportFilters.EndDateParameter)?.ResourceKey;

    /// <summary>A problem no date field can show: a changed link, for example.</summary>
    public bool HasGeneralProblem => Problems.Any(p => p.Parameter != OrderReportFilters.StartDateParameter && p.Parameter != OrderReportFilters.EndDateParameter);

    /// <summary>The same number of complete days ending yesterday, as a link, when the period includes today.</summary>
    public string? CompleteDaysUrl { get; init; }
}

public sealed record ReportFilterProblem(string Parameter, string ResourceKey);

/// <summary>One bar-chart column: a local day, its stacked segments (share of the axis, 0 to 100) and its inspection text.</summary>
public sealed record ReportChartColumn(
    DateOnly Date,
    IReadOnlyList<ReportChartSegment> Segments,
    bool IsPartialDay,
    bool IsZero,
    string? AxisLabel,
    bool AxisLabelIsToday,
    string AccessibleLabel,
    string TipTitle,
    IReadOnlyList<ReportChartTipRow> TipRows);

public sealed record ReportChartSegment(string Series, double HeightPercent);

public sealed record ReportChartTipRow(string Label, string Value);

public sealed record ReportChartTick(string Label, double Percent);

public sealed record ReportBarChart(string AccessibleName, IReadOnlyList<ReportChartColumn> Columns, IReadOnlyList<ReportChartTick> Ticks, bool ShowZeroLabels);

public sealed record ReportShareRow(FoodPlatform Platform, int Count, double BarPercent, decimal Share);

public sealed record ReportStripSegment(FoodPlatform Platform, double Weight, decimal Share, string Value);

public sealed record ReportLineChart(string CurrentPath, string PreviousPath, IReadOnlyList<ReportChartTick> Ticks, IReadOnlyList<(string Label, double Percent)> AxisLabels);

public sealed record ReportComparisonRow(string LabelKey, string Current, string Previous, string? Difference, decimal? Change);
