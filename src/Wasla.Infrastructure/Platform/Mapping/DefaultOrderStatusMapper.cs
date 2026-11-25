using Wasla.Application.Abstractions.Platform;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Microsoft.Extensions.Logging;

namespace Wasla.Infrastructure.Platform.Mapping;

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

    // EN: Normalize partner-style strings (RECEIVED, …) plus legacy lowercase mocks into Wasla's shared lifecycle; RECEIVED vs internal Preparing is reconciled at sync merge.
    // TR: Partner stringlerini (RECEIVED, …) ve eski küçük harf mock'larını ortak lifecycle'a map ederiz; RECEIVED ile iç Preparing uyumu sync merge'da toparlanır.
    // Accepting an order starts its preparation (WAS-185), so acceptance strings map to Preparing.
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
            "confirmed" or "accepted" => OrderStatus.Preparing,
            "preparing" => OrderStatus.Preparing,
            "ready" or "ready_for_pickup" => OrderStatus.ReadyForPickup,
            "on_the_way" or "courier" => OrderStatus.OnTheWay,
            "delivered" => OrderStatus.Delivered,
            "cancelled" or "canceled" => OrderStatus.Cancelled,
            "failed" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }

    // EN: Maps ExternalStatus tokens into Wasla lifecycle; VERIFY/PREPARE/HANDOVER-style strings here are mock/dev echoes—not guaranteed production codes.
    // TR: ExternalStatus token'larını Wasla lifecycle'a map eder; VERIFY/PREPARE/HANDOVER burada mock/dev echo'dur—kesin production kodu garantisi yoktur.
    private static OrderStatus MapGetir(string status)
    {
        var u = (status ?? string.Empty).Trim().ToUpperInvariant();
        return u switch
        {
            "CREATED" => OrderStatus.New,
            // VERIFY is what this client's accept call echoes (the restaurant verified the order), so it is the acceptance.
            "VERIFY" or "VERIFIED" => OrderStatus.Preparing,
            "CONFIRMED" or "ACCEPTED" => OrderStatus.Preparing,
            "PREPARE" or "PREPARING" => OrderStatus.Preparing,
            "READY" or "READY_FOR_PICKUP" => OrderStatus.ReadyForPickup,
            "ON_THE_WAY" or "DISPATCHED" or "HANDOVER" => OrderStatus.OnTheWay,
            "DELIVERED" => OrderStatus.Delivered,
            "CANCELLED" or "CANCELED" => OrderStatus.Cancelled,
            "FAILED" => OrderStatus.Failed,
            _ => OrderStatus.New
        };
    }

    // Which order status each recognized Trendyol GO package status means. The recognized statuses, their spellings and
    // the comparison rule (trimmed, case-sensitive, Returned in any casing) are the provider's: TrendyolGoPackageStatus.
    // Picking is the seller's acceptance (the `picked` call) and starts preparation; there is no separate prepare status.
    private static OrderStatus MapTrendyol(string recognizedStatus) => recognizedStatus switch
    {
        TrendyolGoPackageStatus.Created => OrderStatus.New,
        TrendyolGoPackageStatus.Picking => OrderStatus.Preparing,
        TrendyolGoPackageStatus.Invoiced => OrderStatus.ReadyForPickup,
        TrendyolGoPackageStatus.Shipped => OrderStatus.OnTheWay,
        TrendyolGoPackageStatus.Delivered => OrderStatus.Delivered,
        TrendyolGoPackageStatus.Cancelled => OrderStatus.Cancelled,
        TrendyolGoPackageStatus.UnSupplied => OrderStatus.Cancelled,
        TrendyolGoPackageStatus.Returned => OrderStatus.Cancelled,

        // Labels earlier mock data used
        TrendyolGoPackageStatus.New => OrderStatus.New,
        TrendyolGoPackageStatus.TurkishNew => OrderStatus.New,
        TrendyolGoPackageStatus.TurkishApproved => OrderStatus.Preparing,
        TrendyolGoPackageStatus.TurkishPreparing => OrderStatus.Preparing,
        TrendyolGoPackageStatus.TurkishReady => OrderStatus.ReadyForPickup,
        TrendyolGoPackageStatus.TurkishOnTheWay => OrderStatus.OnTheWay,
        TrendyolGoPackageStatus.TurkishDelivery => OrderStatus.Delivered,
        TrendyolGoPackageStatus.TurkishDelivered => OrderStatus.Delivered,
        TrendyolGoPackageStatus.TurkishCancel => OrderStatus.Cancelled,
        TrendyolGoPackageStatus.TurkishCancelled => OrderStatus.Cancelled,
        TrendyolGoPackageStatus.TurkishFailed => OrderStatus.Failed,

        // A status the provider recognizes but this mapper does not map yet; DefaultOrderStatusMapperTests
        // fails until it is added above.
        _ => OrderStatus.New
    };

    private OrderStatus MapTrendyolWithWarning(string status)
    {
        if (TrendyolGoPackageStatus.Recognize(status) is { } recognized)
            return MapTrendyol(recognized);

        // EN: Warn when Trendyol sends an unknown status string so we do not silently assume New without a trace.
        // TR: Trendyol bilinmeyen statü string'i gönderdiğinde uyarı; sessizce New varsaymayalım.
        // A blank status never gets here: MapToInternalStatus returns New for it first.
        _logger.LogWarning("Unknown Trendyol status '{ExternalStatus}', defaulting to New", TrendyolGoPackageStatus.Normalize(status));
        return OrderStatus.New;
    }
}
