namespace OrderHub.Application.Abstractions.Orders.Services;

public interface IOrderSyncService
{
    Task SyncCustomerAsync(Guid customerId, CancellationToken ct);

    /// <summary>
    /// Sync a single customer and return aggregated counters for logging/telemetry.
    /// This must not include any secrets (credentials, connection strings, etc.).
    /// </summary>
    Task<OrderSyncCustomerResult> SyncCustomerWithResultAsync(Guid customerId, CancellationToken ct);
}

