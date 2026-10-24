namespace Wasla.Application.Orders;

/// <summary>Sort keys the orders list accepts for its time columns (WAS-125).</summary>
public static class OrderListSort
{
    /// <summary>The operational order time: the provider's creation time, otherwise the first-import time. The default.</summary>
    public const string OrderedAt = "orderedAt";

    /// <summary>When Wasla first stored the order.</summary>
    public const string ReceivedAt = "receivedAt";
}
