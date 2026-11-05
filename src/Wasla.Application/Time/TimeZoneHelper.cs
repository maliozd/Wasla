namespace Wasla.Application.Time;

/// <summary>
/// Restaurant timezone helpers (MVP).
/// </summary>
/// <remarks>
/// TODO: Replace hardcoded Turkey timezone with a per-tenant timezone setting (e.g. Tenant.TimeZoneId).
/// </remarks>
public static class TimeZoneHelper
{
    public static TimeZoneInfo ResolveTurkeyTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
        }
    }
}
