using Wasla.Domain.Enums;

namespace Wasla.Application.Orders;

/// <summary>
/// Which orders the operational Live Screen shows: every active order, and a delivered order for
/// <see cref="RecentDeliveredWindow"/>. After that the order leaves the Live Screen only; it stays on the Orders page
/// and in order history. The guided-demo success copy shows this same window, so there is one source for the number.
/// </summary>
public static class LiveScreenVisibility
{
    public static readonly TimeSpan RecentDeliveredWindow = TimeSpan.FromMinutes(2);

    /// <summary>The statuses the Live Screen counts as active (its header's active count); the snapshot holds every one.</summary>
    public static readonly OrderStatus[] ActiveStatuses =
    [
        OrderStatus.New,
        OrderStatus.Accepted,
        OrderStatus.Preparing,
        OrderStatus.ReadyForPickup,
        OrderStatus.OnTheWay
    ];

    public static bool IsActive(OrderStatus status) => Array.IndexOf(ActiveStatuses, status) >= 0;
}
