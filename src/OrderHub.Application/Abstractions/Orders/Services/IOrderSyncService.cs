namespace OrderHub.Application.Abstractions.Orders.Services;

public interface IOrderSyncService
{
    Task SyncCustomerAsync(Guid customerId, CancellationToken ct);
}

