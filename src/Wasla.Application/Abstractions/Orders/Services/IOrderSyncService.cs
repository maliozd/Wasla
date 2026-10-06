namespace Wasla.Application.Abstractions.Orders.Services;

public interface IOrderSyncService
{
    Task SyncCustomerAsync(Guid customerId, CancellationToken ct);

    /// <summary>
    /// Sync a single customer and return aggregated counters for logging/telemetry.
    /// This must not include any secrets (credentials, connection strings, etc.).
    /// </summary>
    Task<OrderSyncCustomerResult> SyncCustomerWithResultAsync(Guid customerId, CancellationToken ct);

    /// <summary>
    /// One recovery turn after an outage: fetches and persists the oldest missing window of each connection whose
    /// order history is behind and whose last current-window sync succeeded. Lower priority than
    /// <see cref="SyncCustomerWithResultAsync"/>; <see cref="OrderSyncCustomerResult.BackfillPending"/> says whether
    /// more turns are needed.
    /// </summary>
    Task<OrderSyncCustomerResult> BackfillCustomerAsync(Guid customerId, CancellationToken ct);
}

