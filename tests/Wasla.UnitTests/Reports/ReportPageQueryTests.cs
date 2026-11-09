using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.Web.Models.Reports;

namespace Wasla.UnitTests.Reports;

/// <summary>The report page's URL state and the small shared rules it added: the cancellation rate and the preceding period.</summary>
public sealed class ReportPageQueryTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneHelper.ResolveTurkeyTimeZone();

    private static ReportPageQuery Parse(string query) => ReportPageQuery.From(new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)));

    private static OrderReportFilter Filter(string preset, string? start = null, string? end = null, string? platform = null) =>
        OrderReportFilters.Resolve(preset, start, end, platform, Now, Zone).Filter!;

    [Fact]
    public void NoQuery_IsTheLast30DaysOfTheOverview_AndWritesABareUrl()
    {
        var query = Parse("");

        Assert.Equal("last30days", query.Preset);
        Assert.Equal(ReportSections.Overview, query.Section);
        Assert.False(query.HasNonDefaultFilter);
        Assert.Equal("/reports", query.Url());
    }

    [Theory]
    [InlineData("?preset=today")]
    [InlineData("?preset=yesterday&platform=TrendyolYemek")]
    [InlineData("?preset=custom&startDate=2026-10-01&endDate=2026-10-05&compare=1")]
    [InlineData("?preset=last7days&section=platforms")]
    [InlineData("?platform=GetirYemek&section=cancellations")]
    public void Url_RoundTrips(string url) => Assert.Equal("/reports" + url, Parse(url).Url());

    [Fact]
    public void DatesWithoutAPreset_AreACustomRange() => Assert.True(Parse("?startDate=2026-10-01&endDate=2026-10-02").IsCustom);

    [Fact]
    public void AnUnreleasedSection_FallsBackToTheOverview() => Assert.Equal(ReportSections.Overview, Parse("?section=products").Section);

    [Fact]
    public void AParameterGivenTwice_IsReported() => Assert.Equal(["platform"], Parse("?platform=1&platform=2").DuplicatedParameters);

    [Theory]
    [InlineData("?platform=1&platform=2")]
    [InlineData("?preset=today&preset=yesterday")]
    [InlineData("?preset=custom&startDate=2026-10-01&startDate=2026-10-02&endDate=2026-10-05")]
    [InlineData("?preset=today&startDate=2026-10-01")]
    [InlineData("?platform=%203")]
    public void ARefusedQuery_IsWrittenBackAsSubmitted(string query) =>
        Assert.Equal("/reports" + query + (query.Contains('?') ? "&" : "?") + "section=platforms", Parse(query).UrlForSection(ReportSections.Platforms));

    [Fact]
    public void Reset_DropsRepeatedFilters() => Assert.Equal("/reports?section=platforms", Parse("?platform=1&platform=2&section=platforms").ResetUrl());

    [Fact]
    public void AChosenPreset_ReplacesARepeatedPreset_AndKeepsARepeatedPlatform() =>
        Assert.Equal("/reports?preset=today&platform=1&platform=2", Parse("?preset=a&preset=b&platform=1&platform=2").UrlForPreset("today"));

    [Fact]
    public void ReleasedSections_AreExactlyTheDataSupportedOnes() =>
        Assert.Equal(["overview", "platforms", "cancellations"], ReportSections.All);

    [Fact]
    public void PresetLinks_DropCustomDates_AndKeepTheRest()
    {
        var query = Parse("?preset=custom&startDate=2026-10-01&endDate=2026-10-05&platform=GetirYemek&section=platforms");
        Assert.Equal("/reports?preset=today&platform=GetirYemek&section=platforms", query.UrlForPreset("today"));
        Assert.Equal("/reports?section=platforms", query.ResetUrl());
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(4, 1, 0.25)]
    [InlineData(8, 0, 0.0)]
    public void CancellationRate_IsCancelledOverEveryOrder_NullWithoutOrders(int orders, int cancelled, double? expected)
    {
        var figures = new OrderReportFigures(orders, 0m, 0, 0m, cancelled, 0, orders - cancelled, 0, 0, 0, cancelled, 0, cancelled);
        Assert.Equal(expected is null ? null : (decimal)expected.Value, figures.CancellationRate);
    }

    [Fact]
    public void PrecedingPeriod_HasEqualLength_AndEndsTheDayBefore_ForTheSamePlatform()
    {
        var preceding = OrderReportFilters.PrecedingPeriod(Filter("custom", "2026-10-06", "2026-10-10", "GetirYemek"), Now)!;

        Assert.Equal(new DateOnly(2026, 10, 1), preceding.Period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 5), preceding.Period.EndDate);
        Assert.Equal(5, preceding.Period.DayCount);
        Assert.Equal(FoodPlatform.GetirYemek, preceding.Platform);
        Assert.True(preceding.Period.IsComplete);
    }

    [Fact]
    public void PrecedingPeriod_OfYesterday_IsTheDayBefore()
    {
        var preceding = OrderReportFilters.PrecedingPeriod(Filter("yesterday"), Now)!;
        Assert.Equal(new DateOnly(2026, 11, 6), preceding.Period.StartDate);
        Assert.Equal(new DateOnly(2026, 11, 6), preceding.Period.EndDate);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("last7days")]
    [InlineData("last30days")]
    public void PrecedingPeriod_OfAPeriodThatIncludesToday_DoesNotExist(string preset) =>
        Assert.Null(OrderReportFilters.PrecedingPeriod(Filter(preset), Now));

    [Fact]
    public void PrecedingPeriod_BeforeTheFirstAcceptedDate_DoesNotExist() =>
        Assert.Null(OrderReportFilters.PrecedingPeriod(Filter("custom", "2000-01-01", "2000-01-05"), Now));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(38, 40)]
    [InlineData(7, 8)]
    [InlineData(150, 200)]
    [InlineData(16759, 20000)]
    public void NiceMax_IsARoundEvenAxis(int max, int expected) => Assert.Equal(expected, ReportPresenter.NiceMax(max));

    /// <summary>The top of the integer range, where multiplying the axis as an int wraps around and never ends.</summary>
    [Theory]
    [InlineData(1_000_000_000, "1000000000")]
    [InlineData(1_000_000_001, "2000000000")]
    [InlineData(int.MaxValue, "4000000000")]
    public async Task NiceMax_OfAVeryLargeCount_Ends_WithoutWrappingAround(int max, string expected)
    {
        var axis = await BoundedAsync(() => (decimal)ReportPresenter.NiceMax(max));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), axis);
    }

    public static TheoryData<decimal, string> AxisThresholds => new()
    {
        { 0.50m, "₺2" },
        { 9m, "₺10" },
        { 16_759.25m, "₺20,000" },
        { 2_147_483_646.50m, "₺4,000,000,000" },
        { 2_147_483_647m, "₺4,000,000,000" },
        { 2_147_483_647.01m, "₺4,000,000,000" },
        { 2_147_483_648m, "₺4,000,000,000" },
        { 4_000_000_000m, "₺4,000,000,000" },
        { 4_000_000_000.01m, "₺5,000,000,000" },
        { 9_999_999_999.99m, "₺10,000,000,000" },
        { 10_000_000_000m, "₺10,000,000,000" },
        { 10_000_000_000.01m, "₺20,000,000,000" },
        { 10_000_000_000_000_000.02m, "₺20,000,000,000,000,000" },
        { 10_000_000_000_000_000_000_000_000_000m, "₺10,000,000,000,000,000,000,000,000,000" },
        { 60_000_000_000_000_000_000_000_000_000m, "₺60,000,000,000,000,000,000,000,000,000" },
        // No round axis fits above this: an axis of 80e27 is past the largest decimal.
        { 60_000_000_000_000_000_000_000_000_001m, "₺60,000,000,000,000,000,000,000,000,001" },
        { decimal.MaxValue, "₺79,228,162,514,264,337,593,543,950,335" }
    };

    [Theory]
    [MemberData(nameof(AxisThresholds))]
    public async Task ValueChart_ScalesAnyAmount_AndLeavesItExact(decimal value, string expectedTop)
    {
        using var culture = new CultureScope("en-US");
        var report = OrderReportService.Build(Filter("yesterday"), [new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, new DateOnly(2026, 11, 7), 1, value)], Now);

        var chart = await BoundedAsync(() => new ReportPresenter(new KeyLocalizer()).ValueChart(report));

        Assert.Equal(expectedTop, chart.Ticks[0].Label);
        var height = Assert.Single(Assert.Single(chart.Columns).Segments).HeightPercent;
        Assert.InRange(height, 0d, 100d);
        Assert.Equal(value, report.Summary.CompletedOrderValue);
        Assert.Contains(ReportFormat.Money(value), Assert.Single(chart.Columns).TipRows.Select(r => r.Value));
    }

    [Fact]
    public async Task ValueChart_ShowsTheExactAmount_AboveTheIntegerRange()
    {
        using var culture = new CultureScope("en-US");
        var report = OrderReportService.Build(Filter("yesterday"), [new(FoodPlatform.TrendyolYemek, OrderStatus.Delivered, new DateOnly(2026, 11, 7), 3, 2_147_483_648.57m)], Now);

        var chart = await BoundedAsync(() => new ReportPresenter(new KeyLocalizer()).ValueChart(report));

        Assert.Contains("₺2,147,483,648.57", Assert.Single(chart.Columns).TipRows.Select(r => r.Value));
        Assert.Equal("₺2,000,000,000", chart.Ticks[1].Label);
        Assert.Equal(53.69, Assert.Single(Assert.Single(chart.Columns).Segments).HeightPercent, 2);
    }

    [Fact]
    public async Task OrdersChart_ScalesTheLargestCount()
    {
        using var culture = new CultureScope("en-US");
        var report = OrderReportService.Build(Filter("yesterday"), [new(FoodPlatform.TrendyolYemek, OrderStatus.New, new DateOnly(2026, 11, 7), int.MaxValue, 1m)], Now);

        var chart = await BoundedAsync(() => new ReportPresenter(new KeyLocalizer()).OrdersChart(report));

        Assert.Equal(["4,000,000,000", "2,000,000,000", "0"], chart.Ticks.Select(t => t.Label));
    }

    [Theory]
    [InlineData("₺100,000", "")]
    [InlineData("₺4,000,000,000", "--rpt-axis-gutter:min(6.8rem, 45%)")]
    [InlineData("₺1,000,000,000,000", "--rpt-axis-gutter:min(8.6rem, 45%)")]
    public void AxisGutter_GrowsOnlyForLabelsTheDefaultCannotHold(string label, string expected) =>
        Assert.Equal(expected, ReportChartAxis.GutterStyle([new ReportChartTick(label, 100), new ReportChartTick("0", 0)]));

    /// <summary>Scaling that wraps around never returns, so a chart is built under a time limit and the failure is reported.</summary>
    private static Task<T> BoundedAsync<T>(Func<T> build) =>
        Task.Run(build, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _culture;
    }

    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
