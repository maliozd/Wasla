using System.Globalization;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Microsoft.Extensions.Localization;

namespace Wasla.Web.Ui;

public static class OrderUiHelper
{
    public static string StatusBadgeClass(OrderStatus status) => status switch
    {
        OrderStatus.New => "wasla-dash-status wasla-dash-status--new",
        OrderStatus.Accepted => "wasla-dash-status wasla-dash-status--accepted",
        OrderStatus.Preparing => "wasla-dash-status wasla-dash-status--progress",
        OrderStatus.ReadyForPickup => "wasla-dash-status wasla-dash-status--ready",
        OrderStatus.OnTheWay => "wasla-dash-status wasla-dash-status--on-the-way",
        OrderStatus.Delivered => "wasla-dash-status wasla-dash-status--delivered",
        OrderStatus.Cancelled => "wasla-dash-status wasla-dash-status--cancelled",
        OrderStatus.Failed => "wasla-dash-status wasla-dash-status--failed",
        _ => "wasla-dash-status"
    };

    public static (TimeZoneInfo TimeZone, DateOnly Today) GetTurkeyDisplayContext() =>
        (TimeZoneHelper.ResolveTurkeyTimeZone(), OrdersReceivedAtQueryRange.GetTurkeyLocalToday());

    public static string FormatReceivedAtUtc(DateTime receivedAtUtc, TimeZoneInfo timeZone, DateOnly turkeyToday)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc),
            timeZone);

        return FormatReceivedAtLocal(local, turkeyToday);
    }

    public static string FormatReceivedAtLocal(DateTime local, DateOnly turkeyToday) =>
        DateOnly.FromDateTime(local) == turkeyToday
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// Culture-aware amount with two fraction digits and no currency symbol.
    /// There is no authoritative currency on the order.
    /// </summary>
    public static string FormatAmount(decimal amount) =>
        amount.ToString("N2", CultureInfo.CurrentCulture);

    public static string FormatElapsed(DateTime receivedAtUtc, DateTime serverTimeUtc, IStringLocalizer localizer)
    {
        var start = receivedAtUtc.Kind == DateTimeKind.Utc
            ? receivedAtUtc
            : DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc);
        var end = serverTimeUtc.Kind == DateTimeKind.Utc
            ? serverTimeUtc
            : DateTime.SpecifyKind(serverTimeUtc, DateTimeKind.Utc);
        var minutes = (int)Math.Floor((end - start).TotalMinutes);
        if (minutes < 0) minutes = 0;
        var key = minutes >= 60
            ? "Orders.LiveScreen.ElapsedHoursMinutes"
            : "Orders.LiveScreen.ElapsedMinutes";
        return minutes >= 60
            ? string.Format(CultureInfo.CurrentCulture, localizer[key].Value, minutes / 60, minutes % 60)
            : string.Format(CultureInfo.CurrentCulture, localizer[key].Value, minutes);
    }

    public static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    public static string? PhoneTelHref(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        return digits.Length >= 7 ? "tel:" + digits : null;
    }

    public static string PaymentMethodLabel(PaymentMethod method, IStringLocalizer localizer) =>
        method == PaymentMethod.Unknown ? "—" : localizer[$"PaymentMethod.{method}"].Value;
}
