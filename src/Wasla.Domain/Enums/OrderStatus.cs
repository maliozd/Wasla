namespace Wasla.Domain.Enums;

/// <summary>
/// Our internal canonical order status. Each platform's raw status is mapped to one of these.
/// The raw platform status is still stored in Order.PlatformStatus for reference.
/// </summary>
public enum OrderStatus
{
    New = 0,
    // 1 was Accepted. Accepting an order starts its preparation (WAS-185), so the value is retired: never reuse it.
    Preparing = 2,
    ReadyForPickup = 3,
    OnTheWay = 4,
    Delivered = 5,
    Cancelled = 6,
    Failed = 7
}
