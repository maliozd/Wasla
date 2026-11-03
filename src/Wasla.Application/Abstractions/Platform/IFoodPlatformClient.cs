using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Application.Platform.Dtos;

namespace Wasla.Application.Abstractions.Platform;

public interface IFoodPlatformClient
{
    FoodPlatform Platform { get; }

    /// <summary>
    /// Longest <see cref="OrderFetchWindow"/> this client requests in one fetch, or null when the client does not
    /// filter by the window. Synchronization splits a longer interval into consecutive windows of at most this length.
    /// </summary>
    TimeSpan? MaxFetchWindow { get; }

    /// <summary>
    /// Returns the orders of one fetch across all pages, or throws; a partial result is never returned as success.
    /// A client with a <see cref="MaxFetchWindow"/> returns the orders the provider reports as modified within
    /// <paramref name="window"/>.
    /// </summary>
    Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(
        PlatformConnection connection,
        OrderFetchWindow window,
        CancellationToken ct);

    Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct);
    Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);
    Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);
    Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct);

    /// <summary>
    /// Rejects the whole order on the provider. <paramref name="reasonId"/> is the operator's reason, already checked
    /// against <c>OrderRejectReasons</c> for the order's platform (null for a platform without reasons). The client
    /// derives the rest of its request (item ids) from <paramref name="order"/> and throws, before any request, when the
    /// stored order cannot supply it.
    /// </summary>
    Task RejectOrderAsync(PlatformConnection connection, Order order, int? reasonId, CancellationToken ct);
}

