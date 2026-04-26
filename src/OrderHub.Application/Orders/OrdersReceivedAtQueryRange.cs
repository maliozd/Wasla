using OrderHub.Application.Time;

namespace OrderHub.Application.Orders;

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
    /// Parses yyyy-MM-dd query strings and builds the UTC range. When both strings are missing, both parsed values are null.
    /// </summary>
    public static (DateTime? UtcStartInclusive, DateTime? UtcEndExclusive) FromWebQueryStrings(
        string? startDate,
        string? endDate,
        out DateOnly? startParsed,
        out DateOnly? endParsed)
    {
        startParsed = null;
        endParsed = null;
        if (!string.IsNullOrWhiteSpace(startDate) && DateOnly.TryParse(startDate, out var s))
            startParsed = s;
        if (!string.IsNullOrWhiteSpace(endDate) && DateOnly.TryParse(endDate, out var e))
            endParsed = e;

        MapLocalDatesToUtcRange(startParsed, endParsed, out var utcStart, out var utcEnd);
        return (utcStart, utcEnd);
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
