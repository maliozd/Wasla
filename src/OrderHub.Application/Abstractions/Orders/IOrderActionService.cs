using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Orders;

public interface IOrderActionService
{
    /// <summary>
    /// MVP: update the internal order status locally (no provider API call yet).
    /// Returns false if order not found for this customer or not actionable.
    /// </summary>
    Task<bool> TryUpdateStatusAsync(Guid customerId, Guid orderId, OrderStatus newStatus, CancellationToken ct);
}

