using System.Globalization;
using OrderHub.Application.Orders;
using OrderHub.Application.Time;
using OrderHub.Domain.Enums;
using Microsoft.Extensions.Localization;

namespace OrderHub.Web.Ui;

public static class OrderUiHelper
{
    public static string StatusBadgeClass(OrderStatus status) => status switch
    {
        OrderStatus.New => "oh-dash-status oh-dash-status--new",
        OrderStatus.Accepted => "oh-dash-status oh-dash-status--accepted",
        OrderStatus.Preparing or OrderStatus.ReadyForPickup or OrderStatus.OnTheWay => "oh-dash-status oh-dash-status--progress",
        OrderStatus.Delivered => "oh-dash-status oh-dash-status--delivered",
        OrderStatus.Cancelled => "oh-dash-status oh-dash-status--cancelled",
        OrderStatus.Failed => "oh-dash-status oh-dash-status--failed",
        _ => "oh-dash-status"
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
