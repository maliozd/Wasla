namespace Wasla.Domain.Enums;

/// <summary>
/// How a <see cref="OrderStatus.Cancelled"/> order came to end, as far as Wasla knows (WAS-200). The status is the
/// outcome; this is who initiated it. Stored on <c>Order.CancellationOrigin</c>, where null means unknown: every order
/// that ended before the column existed, and every provider status whose meaning is not verified. Numbers are stored
/// and never reused.
/// </summary>
public enum OrderCancellationOrigin
{
    /// <summary>An operator rejected a New order through Wasla and the provider accepted the rejection.</summary>
    OperatorRejected = 1,

    /// <summary>An operator cancelled an accepted order through Wasla. No such action exists yet, so nothing writes it.</summary>
    OperatorCancelled = 2,

    /// <summary>
    /// The provider reported a cancellation it documents as made by the restaurant, and no Wasla action recorded it:
    /// made on the provider's own panel, or a Wasla rejection whose local save did not happen. Wasla does not know who
    /// made it or whether it was a rejection or a cancellation, so a report must not count it as a known cancellation.
    /// </summary>
    RestaurantOnPlatform = 3,

    /// <summary>
    /// The provider reported a cancellation it documents as not coming from the restaurant. That may be the order
    /// customer, the courier or the provider itself; this value never claims the customer. A reason code can name a more
    /// specific cause, but a cause does not say who pressed cancel either.
    /// </summary>
    ProviderOrCustomer = 4
}
