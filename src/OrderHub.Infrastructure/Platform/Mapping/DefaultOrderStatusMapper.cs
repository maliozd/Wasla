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

    // EN: Normalize partner-style strings (RECEIVED, …) plus legacy lowercase mocks into OrderHub's shared lifecycle; RECEIVED vs internal Accepted is reconciled at sync merge.
    // TR: Partner stringlerini (RECEIVED, …) ve eski küçük harf mock'larını ortak lifecycle'a map ederiz; RECEIVED ile iç Accepted uyumu sync merge'da toparlanır.
    private static OrderStatus MapYemeksepeti(string status)
    {
        var s = (status ?? string.Empty).Trim();
        if (s.Length == 0) return OrderStatus.New;

        var u = s.ToUpperInvariant();
        if (u is "RECEIVED") return OrderStatus.New;
        if (u is "READY_FOR_PICKUP") return OrderStatus.ReadyForPickup;
        if (u is "DISPATCHED") return OrderStatus.OnTheWay;
        if (u is "DELIVERED") return OrderStatus.Delivered;
        if (u is "CANCELLED" or "CANCELED") return OrderStatus.Cancelled;

        var sLower = s.ToLowerInvariant();
        if (sLower is "received") return OrderStatus.New;

        return sLower switch
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

    // EN: Maps ExternalStatus tokens into OrderHub lifecycle; VERIFY/PREPARE/HANDOVER-style strings here are mock/dev echoes—not guaranteed production codes.
    // TR: ExternalStatus token'larını OrderHub lifecycle'a map eder; VERIFY/PREPARE/HANDOVER burada mock/dev echo'dur—kesin production kodu garantisi yoktur.
    private static OrderStatus MapGetir(string status)
    {
        var u = (status ?? string.Empty).Trim().ToUpperInvariant();
        return u switch
        {
            "CREATED" => OrderStatus.New,
            "VERIFY" or "VERIFIED" => OrderStatus.Accepted,
            "CONFIRMED" or "ACCEPTED" => OrderStatus.Accepted,
            "PREPARE" or "PREPARING" => OrderStatus.Preparing,
            "READY" or "READY_FOR_PICKUP" => OrderStatus.ReadyForPickup,
            "ON_THE_WAY" or "DISPATCHED" or "HANDOVER" => OrderStatus.OnTheWay,
            "DELIVERED" => OrderStatus.Delivered,
            "CANCELLED" or "CANCELED" => OrderStatus.Cancelled,
            "FAILED" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }

    // EN: Trendyol GO package statuses (and a few Turkish mock labels) fold into the same shared OrderHub statuses as the real integration.
    // TR: Trendyol GO paket statüleri (ve birkaç Türkçe mock etiketi) gerçek entegrasyonla aynı ortak OrderHub statülerine indirgenir.
    private static OrderStatus MapTrendyol(string status)
    {
        var t = (status ?? string.Empty).Trim();
        if (t.Length == 0) return OrderStatus.New;

        if (t.Equals("Returned", StringComparison.OrdinalIgnoreCase))
            return OrderStatus.Cancelled;

        return t switch
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
            // EN: Warn when Trendyol sends an unknown status string so we do not silently assume New without a trace.
            // TR: Trendyol bilinmeyen statü string'i gönderdiğinde uyarı; sessizce New varsaymayalım.
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
