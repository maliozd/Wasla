using OrderHub.Application.Abstractions.Platform;
using OrderHub.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace OrderHub.Infrastructure.Platform.Mapping;

public sealed class DefaultOrderStatusMapper : IOrderStatusMapper
{
    private readonly ILogger<DefaultOrderStatusMapper> _logger;

    public DefaultOrderStatusMapper(ILogger<DefaultOrderStatusMapper> logger)
    {
        _logger = logger;
    }

    public OrderStatus MapToInternalStatus(FoodPlatform platform, string externalStatus)
    {
        var s = (externalStatus ?? string.Empty).Trim();

        if (s.Length == 0) return OrderStatus.New;

        return platform switch
        {
            FoodPlatform.Yemeksepeti => MapYemeksepeti(s),
            FoodPlatform.GetirYemek => MapGetir(s),
            FoodPlatform.TrendyolYemek => MapTrendyolWithWarning(s),
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
        // Trendyol GO (real API) statuses + Turkish mock labels.
        var s = (status ?? string.Empty).Trim();
        if (s.Length == 0) return OrderStatus.New;

        return s switch
        {
            // Trendyol GO
            "Created" => OrderStatus.New,
            "Picking" => OrderStatus.Accepted,
            "Invoiced" => OrderStatus.ReadyForPickup,
            "Shipped" => OrderStatus.OnTheWay,
            "Delivered" => OrderStatus.Delivered,
            "Cancelled" => OrderStatus.Cancelled,
            "UnSupplied" => OrderStatus.Cancelled,

            // Turkish mock labels / UI-ish strings
            "Yeni" or "yeni" => OrderStatus.New,
            "Onaylandı" or "Onaylandi" or "onaylandı" or "onaylandi" => OrderStatus.Accepted,
            "Hazırlanıyor" or "Hazirlaniyor" or "hazırlanıyor" or "hazirlaniyor" => OrderStatus.Preparing,
            "Hazır" or "Hazir" or "hazır" or "hazir" => OrderStatus.ReadyForPickup,
            "Yolda" or "yolda" => OrderStatus.OnTheWay,
            "Teslim" or "Teslim edildi" or "teslim" or "teslim edildi" => OrderStatus.Delivered,
            "İptal" or "Iptal" or "İptal edildi" or "Iptal edildi" or "iptal" or "iptal edildi" => OrderStatus.Cancelled,
            "Başarısız" or "Basarisiz" or "başarısız" or "basarisiz" => OrderStatus.Failed,

            _ => OrderStatus.New
        };
    }

    private OrderStatus MapTrendyolWithWarning(string status)
    {
        var mapped = MapTrendyol(status);
        if (mapped == OrderStatus.New)
        {
            // If the external status is not a known "New" synonym, warn.
            var s = (status ?? string.Empty).Trim();
            var isKnownNew =
                s is "Created" or "Yeni" or "yeni" or "new" or "New";

            if (!isKnownNew)
            {
                _logger.LogWarning("Unknown Trendyol status '{ExternalStatus}', defaulting to New", s);
            }
        }
        return mapped;
    }
}

