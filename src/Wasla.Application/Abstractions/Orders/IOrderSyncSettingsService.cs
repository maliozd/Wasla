namespace Wasla.Application.Abstractions.Orders;

public sealed record OrderSyncSettingsResult(bool OrderSyncEnabled);

public interface IOrderSyncSettingsService
{
    Task<OrderSyncSettingsResult> GetAsync(Guid customerId, CancellationToken ct);
    Task<OrderSyncSettingsResult> UpdateAsync(Guid customerId, bool enabled, CancellationToken ct);
}

