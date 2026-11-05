namespace Wasla.Application.Abstractions.Orders.Services;

public interface IOrderSyncService
{
    Task SyncTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Sync a single tenant and return aggregated counters for logging/telemetry.
    /// This must not include any secrets (credentials, connection strings, etc.).
    /// </summary>
    Task<OrderSyncTenantResult> SyncTenantWithResultAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// One recovery turn after an outage: fetches and persists the oldest missing window of each connection whose
    /// order history is behind and whose last current-window sync succeeded. Lower priority than
    /// <see cref="SyncTenantWithResultAsync"/>; <see cref="OrderSyncTenantResult.BackfillPending"/> says whether
    /// more turns are needed.
    /// </summary>
    Task<OrderSyncTenantResult> BackfillTenantAsync(Guid tenantId, CancellationToken ct);
}

