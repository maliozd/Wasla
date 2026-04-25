using System.Globalization;
using OrderHub.Contracts.Enums;

namespace OrderHub.Web.Pages.Helpers;

public static class FormatHelpers
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static string TryCurrency(decimal value) => string.Format(Tr, "{0:N2} ₺", value);

    public static string PlatformName(FoodPlatformDto platform) =>
        platform switch
        {
            FoodPlatformDto.Yemeksepeti => "Yemeksepeti",
            FoodPlatformDto.GetirYemek => "GetirYemek",
            FoodPlatformDto.TrendyolYemek => "TrendyolYemek",
            _ => platform.ToString()
        };

    public static string PlatformColor(FoodPlatformDto platform) =>
        platform switch
        {
            FoodPlatformDto.Yemeksepeti => "#d70f64",
            FoodPlatformDto.GetirYemek => "#5d3ebc",
            FoodPlatformDto.TrendyolYemek => "#ff6b35",
            _ => "#6c6c6c"
        };

    public static string StatusName(OrderStatusDto status) =>
        status switch
        {
            OrderStatusDto.New => "Yeni",
            OrderStatusDto.Accepted => "Kabul Edildi",
            OrderStatusDto.Preparing => "Hazırlanıyor",
            OrderStatusDto.ReadyForPickup => "Hazır",
            OrderStatusDto.OnTheWay => "Yolda",
            OrderStatusDto.Delivered => "Teslim Edildi",
            OrderStatusDto.Cancelled => "İptal",
            OrderStatusDto.Failed => "Başarısız",
            _ => status.ToString()
        };

    public static string StatusBadgeClass(OrderStatusDto status) =>
        status switch
        {
            OrderStatusDto.New => "bg-primary",
            OrderStatusDto.Accepted => "bg-primary",
            OrderStatusDto.Preparing => "bg-info text-dark",
            OrderStatusDto.ReadyForPickup => "bg-warning text-dark",
            OrderStatusDto.OnTheWay => "bg-warning text-dark",
            OrderStatusDto.Delivered => "bg-success",
            OrderStatusDto.Cancelled => "bg-danger",
            OrderStatusDto.Failed => "bg-danger",
            _ => "bg-secondary"
        };

    public static string SyncRateClass(double rate) =>
        rate >= 0.95 ? "border-success" :
        rate >= 0.80 ? "border-warning" :
        "border-danger";

    public static string IstanbulTimeHHmm(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Istanbul);
        return local.ToString("HH:mm", Tr);
    }
}

