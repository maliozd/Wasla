using System.Globalization;

namespace Wasla.PrintBridge.Localization;

internal static class PrintBridgeDateTimeFormatter
{
    public static string FormatDashboard(CultureInfo culture, DateTime localTime) =>
        localTime.ToString(GetDashboardFormat(culture), culture);

    public static string FormatDashboardUtc(CultureInfo culture, DateTime utc) =>
        FormatDashboard(culture, utc.ToLocalTime());

    public static string FormatTooltip(CultureInfo culture, DateTime localTime) =>
        localTime.ToString(GetTooltipFormat(culture), culture);

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
