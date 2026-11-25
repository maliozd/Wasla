using Wasla.Integrations.Platform.Dtos;

namespace Wasla.Integrations.Platform;

/// <summary>
/// One food-delivery platform's provider API. Every operation gets the connection as a
/// <see cref="ProviderConnectionContext"/>: its values as the caller loaded them, and a source the client asks for the
/// credentials when it is about to use them. No operation takes an Orders persistence entity.
/// </summary>
public interface IFoodPlatformClient
{
    ProviderPlatform Platform { get; }

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
        ProviderConnectionContext connection,
        OrderFetchWindow window,
        CancellationToken ct);

    Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct);
    Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct);
    Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct);
    Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct);

    /// <summary>
    /// Rejects the whole order on the provider. <paramref name="reasonId"/> is the operator's reason, already checked by
    /// Orders against the platform's <see cref="ProviderRejectReasons"/> (null for a platform without reasons). The client
    /// derives the rest of its request (item ids) from <paramref name="order"/>'s stored payload and throws, before any
    /// request, when that payload cannot supply it.
    /// </summary>
    Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct);
}
