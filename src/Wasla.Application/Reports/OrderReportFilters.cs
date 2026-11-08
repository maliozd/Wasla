using System.Globalization;
using Wasla.Application.Orders;
using Wasla.Domain.Enums;

namespace Wasla.Application.Reports;

/// <summary>The report periods a user can choose (docs/orders/reporting.md#filter).</summary>
public enum OrderReportPreset
{
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    Custom
}

/// <summary>Why a report filter was refused. Nothing is widened or guessed instead.</summary>
public enum OrderReportFilterError
{
    UnknownPreset,
    StartDateInvalid,
    EndDateInvalid,
    DatesRequireCustomPreset,
    CustomRangeIncomplete,
    RangeInverted,
    RangeTooLong,
    RangeInFuture,
    PlatformInvalid,
    DuplicateParameter
}

/// <summary>
/// The local days a report covers and their UTC bounds. <see cref="DayStartsUtc"/> holds the UTC instant of every
/// local midnight from <see cref="StartDate"/> to the day after <see cref="EndDate"/>, so it has
/// <see cref="DayCount"/> + 1 entries; its first and last are <see cref="UtcStartInclusive"/> and
/// <see cref="UtcEndExclusive"/>.
/// </summary>
public sealed record OrderReportPeriod(
    OrderReportPreset Preset,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeZoneInfo TimeZone,
    IReadOnlyList<DateTime> DayStartsUtc,
    bool IsComplete)
{
    public DateTime UtcStartInclusive => DayStartsUtc[0];

    public DateTime UtcEndExclusive => DayStartsUtc[^1];

    public int DayCount => DayStartsUtc.Count - 1;

    /// <summary>The zone as an IANA id (for example <c>Europe/Istanbul</c>) on every operating system.</summary>
    public string TimeZoneId => OrderReportFilters.IanaId(TimeZone);
}

/// <summary>A valid report filter: the period and, optionally, one platform.</summary>
public sealed record OrderReportFilter(OrderReportPeriod Period, FoodPlatform? Platform);

/// <summary>Either a filter or the reasons it was refused.</summary>
public sealed record OrderReportFilterResult(OrderReportFilter? Filter, IReadOnlyList<OrderReportFilterParameterError> Errors)
{
    public bool IsValid => Filter is not null;
}

/// <summary>A refusal and the query parameter it concerns (<c>preset</c>, <c>startDate</c>, <c>endDate</c>, <c>platform</c>).</summary>
public sealed record OrderReportFilterParameterError(string Parameter, OrderReportFilterError Error);

/// <summary>
/// Resolves the shared report filter of the report page, the export and the Dashboard (WAS-182). Local days are in the
/// given zone (the Türkiye zone in production) and the current day comes from the caller's clock, so tests are
/// deterministic. See docs/orders/reporting.md.
/// </summary>
public static class OrderReportFilters
{
    /// <summary>The longest period one report or export covers, in local days (one quarter).</summary>
    public const int MaxRangeDays = 92;

    public const string PresetParameter = "preset";
    public const string StartDateParameter = "startDate";
    public const string EndDateParameter = "endDate";
    public const string PlatformParameter = "platform";

    private static readonly IReadOnlyDictionary<string, OrderReportPreset> Presets =
        new Dictionary<string, OrderReportPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["today"] = OrderReportPreset.Today,
            ["yesterday"] = OrderReportPreset.Yesterday,
            ["last7days"] = OrderReportPreset.Last7Days,
            ["last30days"] = OrderReportPreset.Last30Days,
            ["custom"] = OrderReportPreset.Custom
        };

    /// <summary>The query-string value of a preset.</summary>
    public static string PresetValue(OrderReportPreset preset) => preset switch
    {
        OrderReportPreset.Today => "today",
        OrderReportPreset.Yesterday => "yesterday",
        OrderReportPreset.Last7Days => "last7days",
        OrderReportPreset.Last30Days => "last30days",
        OrderReportPreset.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    /// <summary>The local day of <paramref name="utcNow"/> in <paramref name="timeZone"/>.</summary>
    public static DateOnly LocalToday(DateTime utcNow, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), timeZone));

    /// <summary>A preset period that needs no dates (every preset except <see cref="OrderReportPreset.Custom"/>).</summary>
    public static OrderReportPeriod ForPreset(OrderReportPreset preset, DateTime utcNow, TimeZoneInfo timeZone)
    {
        var today = LocalToday(utcNow, timeZone);
        var (start, end) = preset switch
        {
            OrderReportPreset.Today => (today, today),
            OrderReportPreset.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            OrderReportPreset.Last7Days => (today.AddDays(-6), today),
            OrderReportPreset.Last30Days => (today.AddDays(-29), today),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "A custom period needs its dates.")
        };
        return Build(preset, start, end, today, timeZone);
    }

    /// <summary>
    /// Resolves the filter as submitted. Missing <paramref name="preset"/> means <c>custom</c> when a date is given,
    /// otherwise <c>today</c>. Every problem is reported; a filter with any error yields no period.
    /// </summary>
    public static OrderReportFilterResult Resolve(
        string? preset,
        string? startDate,
        string? endDate,
        string? platform,
        DateTime utcNow,
        TimeZoneInfo timeZone)
    {
        var errors = new List<OrderReportFilterParameterError>();
        void Fail(string parameter, OrderReportFilterError error) => errors.Add(new(parameter, error));

        var startSupplied = !string.IsNullOrEmpty(startDate);
        var endSupplied = !string.IsNullOrEmpty(endDate);

        OrderReportPreset chosen;
        if (string.IsNullOrEmpty(preset))
        {
            chosen = startSupplied || endSupplied ? OrderReportPreset.Custom : OrderReportPreset.Today;
        }
        else if (!Presets.TryGetValue(preset, out chosen))
        {
            Fail(PresetParameter, OrderReportFilterError.UnknownPreset);
        }

        DateOnly? start = null;
        DateOnly? end = null;
        if (startSupplied)
        {
            if (OrdersReceivedAtQueryRange.TryParseWebDate(startDate, out var s))
                start = s;
            else
                Fail(StartDateParameter, OrderReportFilterError.StartDateInvalid);
        }

        if (endSupplied)
        {
            // 9999-12-31 has no next midnight to end on.
            if (OrdersReceivedAtQueryRange.TryParseWebDate(endDate, out var e) && e < DateOnly.MaxValue)
                end = e;
            else
                Fail(EndDateParameter, OrderReportFilterError.EndDateInvalid);
        }

        var platformValue = ParsePlatform(platform, out var platformValid);
        if (!platformValid)
            Fail(PlatformParameter, OrderReportFilterError.PlatformInvalid);

        if (errors.Any(e => e.Error == OrderReportFilterError.UnknownPreset))
            return new OrderReportFilterResult(null, errors);

        var today = LocalToday(utcNow, timeZone);
        if (chosen != OrderReportPreset.Custom)
        {
            if (startSupplied)
                Fail(StartDateParameter, OrderReportFilterError.DatesRequireCustomPreset);
            if (endSupplied)
                Fail(EndDateParameter, OrderReportFilterError.DatesRequireCustomPreset);
            return errors.Count > 0
                ? new OrderReportFilterResult(null, errors)
                : new OrderReportFilterResult(new OrderReportFilter(ForPreset(chosen, utcNow, timeZone), platformValue), errors);
        }

        if (!startSupplied)
            Fail(StartDateParameter, OrderReportFilterError.CustomRangeIncomplete);
        if (!endSupplied)
            Fail(EndDateParameter, OrderReportFilterError.CustomRangeIncomplete);

        if (start is { } from && end is { } to)
        {
            if (to < from)
                Fail(EndDateParameter, OrderReportFilterError.RangeInverted);
            else if (to > today)
                Fail(EndDateParameter, OrderReportFilterError.RangeInFuture);
            else if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
                Fail(EndDateParameter, OrderReportFilterError.RangeTooLong);
        }

        return errors.Count > 0 || start is null || end is null
            ? new OrderReportFilterResult(null, errors)
            : new OrderReportFilterResult(
                new OrderReportFilter(Build(OrderReportPreset.Custom, start.Value, end.Value, today, timeZone), platformValue),
                errors);
    }

    /// <summary>
    /// A platform given by its defined name (case-insensitive) or its number. Null or empty means every platform. Any
    /// other value, including undefined numbers, signs, spaces or separators, is invalid.
    /// </summary>
    public static FoodPlatform? ParsePlatform(string? value, out bool valid)
    {
        valid = true;
        if (string.IsNullOrEmpty(value))
            return null;

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && Enum.IsDefined(typeof(FoodPlatform), number))
            return (FoodPlatform)number;

        foreach (var platform in Enum.GetValues<FoodPlatform>())
        {
            if (string.Equals(platform.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return platform;
        }

        valid = false;
        return null;
    }

    internal static string IanaId(TimeZoneInfo timeZone)
    {
        if (timeZone.HasIanaId)
            return timeZone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var iana) ? iana : timeZone.Id;
    }

    private static OrderReportPeriod Build(OrderReportPreset preset, DateOnly start, DateOnly end, DateOnly today, TimeZoneInfo timeZone)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        var bounds = new DateTime[days + 1];
        for (var i = 0; i <= days; i++)
            bounds[i] = LocalMidnightUtc(start.AddDays(i), timeZone);
        return new OrderReportPeriod(preset, start, end, timeZone, bounds, IsComplete: end < today);
    }

    /// <summary>
    /// The UTC instant a local day starts. Where a zone skips midnight (a daylight-saving change at 00:00), the day
    /// starts at its first local time that exists.
    /// </summary>
    internal static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var guard = 0;
        while (timeZone.IsInvalidTime(local) && guard++ < 24 * 4)
            local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
