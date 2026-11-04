namespace Wasla.Contracts.Enums;

public enum OrderStatusDto
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

