using OrderHub.Application.Abstractions.Platform;
using OrderHub.Domain.Enums;

namespace OrderHub.Infrastructure.Platform.Mapping;

public sealed class DefaultOrderStatusMapper : IOrderStatusMapper
{
    public OrderStatus MapToInternalStatus(FoodPlatform platform, string externalStatus)
    {
        var s = (externalStatus ?? string.Empty).Trim();

        if (s.Length == 0) return OrderStatus.New;

        return platform switch
        {
            FoodPlatform.Yemeksepeti => MapYemeksepeti(s),
            FoodPlatform.GetirYemek => MapGetir(s),
            FoodPlatform.TrendyolYemek => MapTrendyol(s),
            _ => OrderStatus.New
        };
    }

    private static OrderStatus MapYemeksepeti(string status)
    {
        var s = status.ToLowerInvariant();
        return s switch
        {
            "new" => OrderStatus.New,
            "confirmed" or "accepted" => OrderStatus.Accepted,
            "preparing" => OrderStatus.Preparing,
            "ready" or "ready_for_pickup" => OrderStatus.ReadyForPickup,
            "on_the_way" or "courier" => OrderStatus.OnTheWay,
            "delivered" => OrderStatus.Delivered,
            "cancelled" or "canceled" => OrderStatus.Cancelled,
            "failed" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }

    private static OrderStatus MapGetir(string status)
    {
        var s = status.ToUpperInvariant();
        return s switch
        {
            "CREATED" => OrderStatus.New,
            "CONFIRMED" or "ACCEPTED" => OrderStatus.Accepted,
            "PREPARING" => OrderStatus.Preparing,
            "READY" or "READY_FOR_PICKUP" => OrderStatus.ReadyForPickup,
            "ON_THE_WAY" => OrderStatus.OnTheWay,
            "DELIVERED" => OrderStatus.Delivered,
            "CANCELLED" or "CANCELED" => OrderStatus.Cancelled,
            "FAILED" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }

    private static OrderStatus MapTrendyol(string status)
    {
        // Turkish sample statuses from mocks / common UI labels.
        var s = status.ToLowerInvariant();
        return s switch
        {
            "yeni" => OrderStatus.New,
            "onaylandı" or "onaylandi" => OrderStatus.Accepted,
            "hazırlanıyor" or "hazirlaniyor" => OrderStatus.Preparing,
            "hazır" or "hazir" => OrderStatus.ReadyForPickup,
            "yolda" => OrderStatus.OnTheWay,
            "teslim" or "teslim edildi" => OrderStatus.Delivered,
            "iptal" or "iptal edildi" => OrderStatus.Cancelled,
            "başarısız" or "basarisiz" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }
}

