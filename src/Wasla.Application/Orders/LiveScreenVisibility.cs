namespace Wasla.Application.Orders;

/// <summary>
/// How long the operational Live Screen keeps showing a delivered order. After this the order
/// leaves the Live Screen only; it stays on the Orders page and in order history.
/// The guided-demo success copy shows this same value, so there is one source for the number.
/// </summary>
public static class LiveScreenVisibility
{
    public static readonly TimeSpan RecentDeliveredWindow = TimeSpan.FromMinutes(2);
}
