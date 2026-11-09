using System.Globalization;
using Wasla.Application.Orders;
using Wasla.Web.Ui;

namespace Wasla.Web.Models.Reports;

/// <summary>
/// How the report page writes its values. The UI culture decides separators and date words; the currency is always
/// <see cref="OrderCurrency"/> and dates are Gregorian (the Web cultures are built that way).
/// </summary>
public static class ReportFormat
{
    public const string NoValue = "—";

    /// <summary>The resource key of a platform's name; the Trendyol platform is sold as Trendyol GO.</summary>
    public static string PlatformKey(Domain.Enums.FoodPlatform platform) => platform switch
    {
        Domain.Enums.FoodPlatform.TrendyolYemek => "Orders.PlatformTrendyolGo",
        _ => "Orders.Platform" + platform
    };

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A count axis label, which can be above the largest count.</summary>
    public static string Count(decimal value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Money(decimal value) => OrderUiHelper.FormatMoney(value);

    public static string Money(decimal? value) => value is { } v ? Money(v) : NoValue;

    /// <summary>An axis label: whole currency units.</summary>
    public static string WholeMoney(decimal value)
    {
        var format = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        format.CurrencySymbol = OrderCurrency.Symbol;
        return value.ToString("C0", format);
    }

    /// <summary>A fraction from 0 to 1 as a percentage with one decimal; no value when there is no denominator.</summary>
    public static string Percent(decimal? fraction) => fraction is { } f ? f.ToString("P1", CultureInfo.CurrentCulture) : NoValue;

    public static string SignedCount(int value) => value switch
    {
        > 0 => "+" + Count(value),
        < 0 => CultureInfo.CurrentCulture.NumberFormat.NegativeSign + Count(-value),
        _ => Count(0)
    };

    public static string SignedMoney(decimal value) => value switch
    {
        > 0 => "+" + Money(value),
        < 0 => CultureInfo.CurrentCulture.NumberFormat.NegativeSign + Money(-value),
        _ => Money(0m)
    };

    public static string SignedPercent(decimal fraction) => fraction switch
    {
        > 0 => "+" + Percent(fraction),
        < 0 => CultureInfo.CurrentCulture.NumberFormat.NegativeSign + Percent(-fraction),
        _ => Percent(0m)
    };

    public static string Day(DateOnly date) => date.ToString("d MMM", CultureInfo.CurrentCulture);

    public static string DayWithWeekday(DateOnly date) => date.ToString("d MMM, ddd", CultureInfo.CurrentCulture);

    public static string DayWithYear(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"10 Eki - 8 Kas 2026", with the year on both ends only when they differ, and a single date for one day.</summary>
    public static string Range(DateOnly start, DateOnly end)
    {
        if (start == end)
            return DayWithYear(start);
        return start.Year == end.Year
            ? $"{Day(start)} - {DayWithYear(end)}"
            : $"{DayWithYear(start)} - {DayWithYear(end)}";
    }

    public static string Time(DateTime local) => local.ToString("t", CultureInfo.CurrentCulture);
}
