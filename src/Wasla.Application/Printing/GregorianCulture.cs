using System.Collections.Concurrent;
using System.Globalization;

namespace Wasla.Application.Printing;

/// <summary>
/// Operational timestamps (orders, print jobs, connection and audit times) are always shown in the Gregorian
/// calendar so they stay directly comparable with provider, server and support records. Some cultures default
/// to another calendar (ar-SA uses Umm al-Qura); this returns the same culture with its Gregorian calendar, keeping
/// the language's names, separators and ordering.
/// </summary>
public static class GregorianCulture
{
    private static readonly ConcurrentDictionary<string, CultureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static CultureInfo For(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (culture.DateTimeFormat.Calendar is GregorianCalendar)
            return culture;

        return Cache.GetOrAdd(culture.Name, _ => CreateGregorian(culture));
    }

    private static CultureInfo CreateGregorian(CultureInfo culture)
    {
        var clone = (CultureInfo)culture.Clone();
        var calendar = culture.OptionalCalendars.OfType<GregorianCalendar>()
                           .OrderBy(c => c.CalendarType == GregorianCalendarTypes.Localized ? 0 : 1)
                           .FirstOrDefault()
                       ?? new GregorianCalendar();
        clone.DateTimeFormat.Calendar = calendar;
        return CultureInfo.ReadOnly(clone);
    }
}
