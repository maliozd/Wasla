namespace OrderHub.Domain.Enums;

/// <summary>
/// Our internal canonical order status. Each platform's raw status is mapped to one of these.
/// The raw platform status is still stored in Order.PlatformStatus for reference.
/// </summary>
public enum OrderStatus
{
    New = 0,
    Accepted = 1,
    Preparing = 2,
    ReadyForPickup = 3,
    OnTheWay = 4,
    Delivered = 5,
    Cancelled = 6,
    Failed = 7
}
