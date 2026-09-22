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
        OrderStatus.Preparing or OrderStatus.ReadyForPickup or OrderStatus.OnTheWay => "wasla-dash-status wasla-dash-status--progress",
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
