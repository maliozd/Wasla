using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// Who reports the courier steps. For every current platform the courier belongs to the platform:
/// the restaurant's last action is ReadyForPickup, and OnTheWay (rider collected the order, e.g.
/// Yemeksepeti DISPATCHED) and Delivered arrive through provider synchronization (and later
/// webhooks). The ReadyForPickup → OnTheWay → Delivered transitions stay valid for provider
/// updates; only the restaurant's user commands are refused. Restaurant-owned courier delivery
/// is a future fulfillment-mode decision.
/// </summary>
public static class OrderDeliveryPolicy
{
    public const string UserPickupNotAllowedKey = "Orders.PickupReportedByPlatform";
    public const string UserDeliveryNotAllowedKey = "Orders.DeliveryReportedByPlatform";

    public static bool IsReportedByPlatform(FoodPlatform platform) => platform is
        FoodPlatform.Yemeksepeti
        or FoodPlatform.GetirYemek
        or FoodPlatform.TrendyolYemek;
}
