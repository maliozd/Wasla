using System.Globalization;

namespace Wasla.Web.Ui;

/// <summary>
/// Shows a stored UTC timestamp in the page culture, marked as UTC. Wasla has no tenant or user time zone
/// setting yet, so the value is never converted to the server's local time or presented as local time.
/// </summary>
public static class UtcTimestampPresentation
{
    private const string UtcMarker = "UTC";

    /// <summary>Round-trip ISO 8601 value with a "Z" suffix, for <c>&lt;time datetime&gt;</c> and sort keys.</summary>
    public static string ToIso(DateTime value) =>
        AsUtc(value).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Short date and time in the given culture (default: the current culture), followed by "UTC".</summary>
    public static string ToDisplay(DateTime value, CultureInfo? culture = null) =>
        $"{AsUtc(value).ToString("g", culture ?? CultureInfo.CurrentCulture)} {UtcMarker}";

    // SQL Server datetime2 values come back with DateTimeKind.Unspecified; they are stored as UTC.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
