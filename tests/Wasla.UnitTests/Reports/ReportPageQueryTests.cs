using Microsoft.AspNetCore.Http;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
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
        var figures = new OrderReportFigures(orders, 0m, 0, 0m, cancelled, 0, orders - cancelled, 0);
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
}
