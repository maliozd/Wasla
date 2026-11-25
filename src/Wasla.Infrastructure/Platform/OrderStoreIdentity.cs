using Wasla.Domain.Entities.TenantDb;

namespace Wasla.Infrastructure.Platform;

/// <summary>
/// Whether the platform's current connection is the store an order was imported under (WAS-241). An order action is
/// sent to the provider only with that store: the order's <see cref="Order.StoreId"/> and <see cref="Order.SupplierId"/>
/// must name the connection's <see cref="PlatformConnection.StoreId"/> and <see cref="PlatformConnection.SupplierId"/>
/// (<see cref="ProviderIdentifier.Same"/>; a supplier the order and the connection both lack is the same, one only one of
/// them has is not). Credentials and the executor e-mail are not part of it: they say how Wasla calls the provider, not
/// which store an order belongs to, so replacing them keeps that store's open orders actionable, and an order never
/// holds anything derived from a secret. The connection's id is not part of it either: a store removed and connected
/// again is still the same store.
/// </summary>
internal static class OrderStoreIdentity
{
    /// <summary>The resource key of an action refused because the order's store is not confirmed as the connection's.</summary>
    internal const string NotConfirmedKey = "Orders.StoreNotConfirmed";

    internal static OrderStoreMatch Compare(Order order, PlatformConnection connection)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(connection);

        if (order.StoreId is null)
            return OrderStoreMatch.NotRecorded;

        return ProviderIdentifier.Same(order.StoreId, connection.StoreId) && SameSupplier(order.SupplierId, connection.SupplierId)
            ? OrderStoreMatch.Same
            : OrderStoreMatch.Different;
    }

    private static bool SameSupplier(string? recorded, string? current)
    {
        var recordedMissing = string.IsNullOrWhiteSpace(recorded);
        var currentMissing = string.IsNullOrWhiteSpace(current);
        if (recordedMissing || currentMissing)
            return recordedMissing && currentMissing;
        return ProviderIdentifier.Same(recorded!, current!);
    }
}

/// <summary>How an order's recorded store compares with its platform's current connection.</summary>
internal enum OrderStoreMatch
{
    /// <summary>The connection still has the store and supplier the order was imported under.</summary>
    Same,

    /// <summary>The connection's store or supplier changed since the order was imported.</summary>
    Different,

    /// <summary>The order was stored before Wasla recorded the store it was imported under.</summary>
    NotRecorded
}
