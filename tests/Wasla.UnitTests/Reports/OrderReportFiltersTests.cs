using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Domain.Enums;

namespace Wasla.UnitTests.Reports;

/// <summary>
/// WAS-182: the shared report filter (docs/orders/reporting.md#filter). Türkiye is UTC+3 all year, so a local
/// midnight is 21:00 UTC the day before.
/// </summary>
public sealed class OrderReportFiltersTests
{
    private static readonly TimeZoneInfo Turkey = TimeZoneHelper.ResolveTurkeyTimeZone();

    // 2026-11-07 13:00 in Türkiye.
    private static readonly DateTime Now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static OrderReportFilterResult Resolve(
        string? preset = null, string? start = null, string? end = null, string? platform = null, DateTime? now = null) =>
        OrderReportFilters.Resolve(preset, start, end, platform, now ?? Now, Turkey);

    private static OrderReportFilter Valid(OrderReportFilterResult result)
    {
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => $"{e.Parameter}:{e.Error}")));
        Assert.Empty(result.Errors);
        return result.Filter!;
    }

    [Theory]
    [InlineData("today", "2026-11-07", "2026-11-07", 1)]
    [InlineData("yesterday", "2026-11-06", "2026-11-06", 1)]
    [InlineData("last7days", "2026-11-01", "2026-11-07", 7)]
    [InlineData("last30days", "2026-10-09", "2026-11-07", 30)]
    [InlineData("LAST7DAYS", "2026-11-01", "2026-11-07", 7)]
    public void Presets_CoverLocalCalendarDays_AndRollingOnesIncludeToday(string preset, string start, string end, int days)
    {
        var period = Valid(Resolve(preset)).Period;

        Assert.Equal(DateOnly.Parse(start), period.StartDate);
        Assert.Equal(DateOnly.Parse(end), period.EndDate);
        Assert.Equal(days, period.DayCount);
        Assert.Equal(days + 1, period.DayStartsUtc.Count);
        // Local midnight of the first day and of the day after the last, converted once to UTC.
        Assert.Equal(DateOnly.Parse(start).ToDateTime(new TimeOnly(0, 0)).AddHours(-3), period.UtcStartInclusive);
        Assert.Equal(DateOnly.Parse(end).AddDays(1).ToDateTime(new TimeOnly(0, 0)).AddHours(-3), period.UtcEndExclusive);
        Assert.All(period.DayStartsUtc, b => Assert.Equal(DateTimeKind.Utc, b.Kind));
        Assert.Equal(preset != "yesterday", !period.IsComplete);
        Assert.Equal("Europe/Istanbul", period.TimeZoneId);
    }

    [Fact]
    public void NoPresetAndNoDates_IsToday()
    {
        var filter = Valid(Resolve());
        Assert.Equal(OrderReportPreset.Today, filter.Period.Preset);
        Assert.Equal(new DateOnly(2026, 11, 7), filter.Period.StartDate);
        Assert.Null(filter.Platform);
    }

    [Theory]
    // 20:59:59.9999999 UTC is still 6 November in Türkiye; 21:00:00 UTC is 7 November.
    [InlineData("2026-11-06T20:59:59.9999999Z", "2026-11-06")]
    [InlineData("2026-11-06T21:00:00.0000000Z", "2026-11-07")]
    [InlineData("2026-11-07T20:59:59.0000000Z", "2026-11-07")]
    public void Today_FollowsTheTurkeyLocalDayOfTheClock_NotTheUtcDay(string nowUtc, string expectedToday)
    {
        var now = DateTime.Parse(nowUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var period = Valid(Resolve("today", now: now)).Period;
        Assert.Equal(DateOnly.Parse(expectedToday), period.StartDate);
        Assert.Equal(DateOnly.Parse(expectedToday), period.EndDate);
    }

    [Fact]
    public void Custom_IsInclusiveOfBothDays_WithHalfOpenUtcBounds()
    {
        var filter = Valid(Resolve("custom", "2026-10-01", "2026-10-03", "TrendyolYemek"));

        Assert.Equal(OrderReportPreset.Custom, filter.Period.Preset);
        Assert.Equal(FoodPlatform.TrendyolYemek, filter.Platform);
        Assert.Equal(3, filter.Period.DayCount);
        Assert.Equal([Utc(9, 30, 21), Utc(10, 1, 21), Utc(10, 2, 21), Utc(10, 3, 21)], filter.Period.DayStartsUtc);
        Assert.True(filter.Period.IsComplete);
    }

    [Fact]
    public void DatesWithoutPreset_AreCustom()
    {
        var filter = Valid(Resolve(start: "2026-11-07", end: "2026-11-07"));
        Assert.Equal(OrderReportPreset.Custom, filter.Period.Preset);
        Assert.False(filter.Period.IsComplete);
    }

    [Fact]
    public void Range_OfExactlyTheMaximum_IsAccepted_AndOneMoreDayIsRefused()
    {
        var end = new DateOnly(2026, 11, 7);
        var longest = end.AddDays(-(OrderReportFilters.MaxRangeDays - 1));
        Assert.Equal(92, Valid(Resolve("custom", longest.ToString("yyyy-MM-dd"), "2026-11-07")).Period.DayCount);

        var tooLong = Resolve("custom", longest.AddDays(-1).ToString("yyyy-MM-dd"), "2026-11-07");
        AssertRefused(tooLong, ("endDate", OrderReportFilterError.RangeTooLong));
    }

    public static TheoryData<string?, string?, string?, string?, string> Invalid => new()
    {
        { "last90days", null, null, null, "preset:UnknownPreset" },
        { "Today ", null, null, null, "preset:UnknownPreset" },
        { "today", "2026-11-07", null, null, "startDate:DatesRequireCustomPreset" },
        { "last7days", "2026-11-01", "2026-11-07", null, "startDate:DatesRequireCustomPreset,endDate:DatesRequireCustomPreset" },
        { "custom", null, null, null, "startDate:CustomRangeIncomplete,endDate:CustomRangeIncomplete" },
        { "custom", "2026-11-01", null, null, "endDate:CustomRangeIncomplete" },
        { null, null, "2026-11-01", null, "startDate:CustomRangeIncomplete" },
        { "custom", "2026-11-05", "2026-11-04", null, "endDate:RangeInverted" },
        { "custom", "2026-11-07", "2026-11-08", null, "endDate:RangeInFuture" },
        { "custom", "2026-11-08", "2026-11-08", null, "endDate:RangeInFuture" },
        { "custom", "07.11.2026", "2026-11-07", null, "startDate:StartDateInvalid" },
        { "custom", "2026-11-01", "2026/11/07", null, "endDate:EndDateInvalid" },
        { "custom", "2026-02-30", "2026-03-01", null, "startDate:StartDateInvalid" },
        { "custom", "1999-12-31", "2000-01-01", null, "startDate:StartDateInvalid" },
        { "custom", "2026-11-01", "9999-12-31", null, "endDate:EndDateInvalid" },
        { "custom", "١٤٤٨-٠٥-٠١", "2026-11-07", null, "startDate:StartDateInvalid" },
        { "custom", " 2026-11-01", "2026-11-07", null, "startDate:StartDateInvalid" },
        { "today", null, null, "0", "platform:PlatformInvalid" },
        { "today", null, null, "4", "platform:PlatformInvalid" },
        { "today", null, null, "-1", "platform:PlatformInvalid" },
        { "today", null, null, "+1", "platform:PlatformInvalid" },
        { "today", null, null, " 1", "platform:PlatformInvalid" },
        { "today", null, null, "Trendyol", "platform:PlatformInvalid" },
        { "today", null, null, "1,2", "platform:PlatformInvalid" },
        { "bogus", "x", null, "9", "startDate:StartDateInvalid,platform:PlatformInvalid,preset:UnknownPreset" }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidFilters_AreRefused_WithoutAPeriod(string? preset, string? start, string? end, string? platform, string expected)
    {
        var result = Resolve(preset, start, end, platform);

        Assert.False(result.IsValid);
        Assert.Null(result.Filter);
        var expectedErrors = expected.Split(',').Select(e => e.Split(':')).Select(p => (p[0], Enum.Parse<OrderReportFilterError>(p[1])));
        AssertRefused(result, expectedErrors.ToArray());
    }

    [Theory]
    [InlineData("1", FoodPlatform.Yemeksepeti)]
    [InlineData("2", FoodPlatform.GetirYemek)]
    [InlineData("3", FoodPlatform.TrendyolYemek)]
    [InlineData("yemeksepeti", FoodPlatform.Yemeksepeti)]
    [InlineData("GETIRYEMEK", FoodPlatform.GetirYemek)]
    public void Platform_IsADefinedNameOrNumber(string value, FoodPlatform expected)
    {
        Assert.Equal(expected, Valid(Resolve("today", platform: value)).Platform);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingPlatform_MeansEveryPlatform(string? value)
    {
        Assert.Null(Valid(Resolve("today", platform: value)).Platform);
    }

    [Fact]
    public void PresetValues_RoundTrip()
    {
        foreach (var preset in Enum.GetValues<OrderReportPreset>())
        {
            var value = OrderReportFilters.PresetValue(preset);
            var result = preset == OrderReportPreset.Custom
                ? Resolve(value, "2026-11-01", "2026-11-02")
                : Resolve(value);
            Assert.Equal(preset, Valid(result).Period.Preset);
        }
    }

    [Fact]
    public void DaylightSavingZone_GetsDaysOf23And25Hours_FromTheirOwnMidnights()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

        var spring = Valid(OrderReportFilters.Resolve("custom", "2026-03-28", "2026-03-30", null, now, berlin)).Period;
        Assert.Equal(
            [Utc(3, 27, 23), Utc(3, 28, 23), Utc(3, 29, 22), Utc(3, 30, 22)],
            spring.DayStartsUtc);

        var autumn = Valid(OrderReportFilters.Resolve("custom", "2026-10-24", "2026-10-26", null, now, berlin)).Period;
        Assert.Equal(
            [Utc(10, 23, 22), Utc(10, 24, 22), Utc(10, 25, 23), Utc(10, 26, 23)],
            autumn.DayStartsUtc);
        Assert.Equal("Europe/Berlin", autumn.TimeZoneId);
    }

    [Fact]
    public void Figures_AddUp_AndAverageIsNullWithoutCompletedOrders()
    {
        Assert.Null(OrderReportFigures.Empty.AverageCompletedOrderValue);

        var figures = OrderReportFigures.ForStatus(OrderStatus.Delivered, 3, 100.00m)
                      + OrderReportFigures.ForStatus(OrderStatus.Cancelled, 1, 50.25m)
                      + OrderReportFigures.ForStatus((OrderStatus)1, 1, 7m);

        Assert.Equal(new OrderReportFigures(5, 157.25m, 3, 100.00m, 1, 0, 0, 1), figures);
        // 100 / 3 = 33.333…, rounded to 2 decimals; a midpoint rounds away from zero.
        Assert.Equal(33.33m, figures.AverageCompletedOrderValue);
        Assert.Equal(0.13m, OrderReportFigures.ForStatus(OrderStatus.Delivered, 4, 0.50m).AverageCompletedOrderValue);
    }

    private static void AssertRefused(OrderReportFilterResult result, params (string Parameter, OrderReportFilterError Error)[] expected)
    {
        Assert.False(result.IsValid);
        Assert.Equal(
            expected.Select(e => $"{e.Parameter}:{e.Error}").OrderBy(x => x, StringComparer.Ordinal),
            result.Errors.Select(e => $"{e.Parameter}:{e.Error}").OrderBy(x => x, StringComparer.Ordinal));
    }
}
