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
    /// Rejects the whole order on the provider. The client derives what its provider requires (item ids, a reason) from
    /// <paramref name="order"/> and throws, before any request, when the stored order cannot supply it.
    /// </summary>
    Task RejectOrderAsync(PlatformConnection connection, Order order, CancellationToken ct);
}

