namespace Wasla.Application.Abstractions.Orders;

public sealed record OrderSyncSettingsResult(bool OrderSyncEnabled);

public interface IOrderSyncSettingsService
{
    Task<OrderSyncSettingsResult> GetAsync(Guid tenantId, CancellationToken ct);
    Task<OrderSyncSettingsResult> UpdateAsync(Guid tenantId, bool enabled, CancellationToken ct);
}

