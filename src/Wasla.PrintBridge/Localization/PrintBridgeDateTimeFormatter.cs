using System.Globalization;
using Wasla.Application.Printing;

namespace Wasla.PrintBridge.Localization;

/// <summary>
/// Formats operational times for both desktop windows. Every culture uses the Gregorian calendar (ar-SA would
/// otherwise switch to Umm al-Qura) so job and connection times match server and provider records.
/// </summary>
internal static class PrintBridgeDateTimeFormatter
{
    public static string FormatDashboard(CultureInfo culture, DateTime localTime) =>
        localTime.ToString(GetDashboardFormat(culture), GregorianCulture.For(culture));

    public static string FormatDashboardUtc(CultureInfo culture, DateTime utc) =>
        FormatDashboard(culture, utc.ToLocalTime());

    public static string FormatTooltip(CultureInfo culture, DateTime localTime) =>
        localTime.ToString(GetTooltipFormat(culture), GregorianCulture.For(culture));

    public static string FormatTooltipUtc(CultureInfo culture, DateTime utc) =>
        FormatTooltip(culture, utc.ToLocalTime());

    private static string GetDashboardFormat(CultureInfo culture) =>
        culture.Name switch
        {
            SupportedCultures.Turkish => "dd.MM.yyyy HH:mm",
            SupportedCultures.English => "yyyy-MM-dd HH:mm",
            SupportedCultures.Arabic => "dd/MM/yyyy HH:mm",
            SupportedCultures.Russian => "dd.MM.yyyy HH:mm",
            _ => "yyyy-MM-dd HH:mm"
        };

    private static string GetTooltipFormat(CultureInfo culture) =>
        culture.Name switch
        {
            SupportedCultures.Turkish => "dd.MM.yyyy HH:mm:ss",
            SupportedCultures.English => "yyyy-MM-dd HH:mm:ss",
            SupportedCultures.Arabic => "dd/MM/yyyy HH:mm:ss",
            SupportedCultures.Russian => "dd.MM.yyyy HH:mm:ss",
            _ => "yyyy-MM-dd HH:mm:ss"
        };
}
