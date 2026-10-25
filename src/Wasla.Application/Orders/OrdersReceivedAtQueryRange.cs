using System.Globalization;
using Wasla.Application.Time;

namespace Wasla.Application.Orders;

/// <summary>
/// The web Orders filter's dates as submitted. <see cref="StartDate"/> and <see cref="EndDate"/> are the valid values
/// (shown again in the form); the UTC bounds are set only when the whole filter is valid.
/// </summary>
public sealed record OrdersWebDateFilter(
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool StartInvalid,
    bool EndInvalid,
    bool Inverted,
    DateTime? UtcStartInclusive,
    DateTime? UtcEndExclusive)
{
    public bool IsValid => !StartInvalid && !EndInvalid && !Inverted;
}

/// <summary>
/// Maps restaurant local calendar dates to UTC bounds for filtering <c>Order.ReceivedAt</c> (stored as UTC).
/// Lower bound is inclusive; upper bound is exclusive (local midnight of the day after the selected end date).
/// </summary>
public static class OrdersReceivedAtQueryRange
{
    public static DateOnly GetTurkeyLocalToday()
    {
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return DateOnly.FromDateTime(localNow);
    }

    /// <summary>Default inclusive history window: last 7 local calendar days ending today.</summary>
    public static (DateOnly Start, DateOnly End) GetDefaultHistoryRange()
    {
        var today = GetTurkeyLocalToday();
        return (today.AddDays(-6), today);
    }

    /// <summary>
    /// Converts optional local start/end dates (same calendar semantics as the orders UI) to UTC for querying.
    /// </summary>
    public static void MapLocalDatesToUtcRange(
        DateOnly? startLocal,
        DateOnly? endLocal,
        out DateTime? utcStartInclusive,
        out DateTime? utcEndExclusive)
    {
        utcStartInclusive = null;
        utcEndExclusive = null;
        if (!startLocal.HasValue && !endLocal.HasValue)
            return;

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();

        if (startLocal.HasValue && endLocal.HasValue)
        {
            var a = startLocal.Value;
            var b = endLocal.Value;
            if (a > b)
                (a, b) = (b, a);

            var localStart = a.ToDateTime(TimeOnly.MinValue);
            var localEndExclusive = b.AddDays(1).ToDateTime(TimeOnly.MinValue);
            utcStartInclusive = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tz);
            utcEndExclusive = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localEndExclusive, DateTimeKind.Unspecified), tz);
            return;
        }

        if (startLocal.HasValue)
        {
            var localStart = startLocal.Value.ToDateTime(TimeOnly.MinValue);
            utcStartInclusive = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tz);
        }

        if (endLocal.HasValue)
        {
            var localEndExclusive = endLocal.Value.AddDays(1).ToDateTime(TimeOnly.MinValue);
            utcEndExclusive = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localEndExclusive, DateTimeKind.Unspecified), tz);
        }
    }

    /// <summary>
    /// The machine-readable date of the web filter: what an HTML date input submits and renders, in every UI culture and
    /// always in the Gregorian calendar.
    /// </summary>
    public const string WebDateFormat = "yyyy-MM-dd";

    // No order has an order time before 2000 (OrderSyncService.PlausibleProviderTime), and Hijri years (14xx) cannot pass
    // for Gregorian ones.
    private static readonly DateOnly MinWebDate = new(2000, 1, 1);

    public static string FormatWebDate(DateOnly date) => date.ToString(WebDateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Strict <see cref="WebDateFormat"/> parsing, independent of the request culture: no culture format that could mean
    /// two different days, no other digits and no other calendar.
    /// </summary>
    public static bool TryParseWebDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, WebDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        && date >= MinWebDate;

    /// <summary>
    /// An end day is queried up to its next local midnight, converted to UTC. That midnight must exist (9999-12-31 has no
    /// next day) and its UTC instant must be representable, which a zone behind UTC could push past
    /// <see cref="DateTime.MaxValue"/>. A start day's midnight always converts: dates start at 2000, and a zone behind
    /// UTC moves 9999-12-31 00:00 by at most a few hours.
    /// </summary>
    private static bool HasRepresentableEndBoundary(DateOnly end, TimeZoneInfo tz)
    {
        if (end == DateOnly.MaxValue)
            return false;

        var nextMidnight = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var offset = tz.GetUtcOffset(nextMidnight);
        return offset >= TimeSpan.Zero || DateTime.MaxValue - nextMidnight >= offset.Negate();
    }

    /// <summary>
    /// Parses the web filter's optional start and end dates. A missing value leaves that side open; a value that is not a
    /// valid date, or an end before the start, makes the filter invalid and leaves the UTC range empty, so a caller never
    /// queries with fewer bounds than were supplied.
    /// </summary>
    public static OrdersWebDateFilter ParseWebQueryStrings(string? startDate, string? endDate)
    {
        var startSupplied = !string.IsNullOrWhiteSpace(startDate);
        var endSupplied = !string.IsNullOrWhiteSpace(endDate);
        DateOnly? start = startSupplied && TryParseWebDate(startDate, out var s) ? s : null;
        DateOnly? end = endSupplied && TryParseWebDate(endDate, out var e)
                        && HasRepresentableEndBoundary(e, TimeZoneHelper.ResolveTurkeyTimeZone())
            ? e
            : null;

        var filter = new OrdersWebDateFilter(
            start,
            end,
            StartInvalid: startSupplied && start is null,
            EndInvalid: endSupplied && end is null,
            Inverted: start > end,
            UtcStartInclusive: null,
            UtcEndExclusive: null);
        if (!filter.IsValid)
            return filter;

        MapLocalDatesToUtcRange(start, end, out var utcStart, out var utcEnd);
        return filter with { UtcStartInclusive = utcStart, UtcEndExclusive = utcEnd };
    }

    /// <summary>
    /// API query uses <see cref="DateTime"/>; each value is mapped to a Turkey local calendar date, then the same range rules as the web UI apply.
    /// </summary>
    public static (DateTime? UtcStartInclusive, DateTime? UtcEndExclusive) FromApiDateTimes(
        DateTime? start,
        DateTime? end)
    {
        if (!start.HasValue && !end.HasValue)
            return (null, null);

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        DateOnly? sd = start.HasValue ? ToTurkeyLocalDateOnly(start.Value, tz) : null;
        DateOnly? ed = end.HasValue ? ToTurkeyLocalDateOnly(end.Value, tz) : null;

        MapLocalDatesToUtcRange(sd, ed, out var a, out var b);
        return (a, b);
    }

    private static DateOnly ToTurkeyLocalDateOnly(DateTime dt, TimeZoneInfo tz)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(dt, tz));

        if (dt.Kind == DateTimeKind.Local)
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(dt.ToUniversalTime(), tz));

        var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        var inTurkey = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        return DateOnly.FromDateTime(inTurkey);
    }
}
