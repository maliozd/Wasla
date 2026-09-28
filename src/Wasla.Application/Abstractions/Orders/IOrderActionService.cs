using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public sealed record OrderActionResult(bool Succeeded, string MessageKey);

public interface IOrderActionService
{
    /// <summary>
    /// MVP: update the internal order status locally (no provider API call yet).
    /// Returns false if order not found for this customer or not actionable.
    /// </summary>
    Task<bool> TryUpdateStatusAsync(Guid customerId, Guid orderId, OrderStatus newStatus, CancellationToken ct);

    Task<OrderActionResult> TryApproveAsync(Guid customerId, Guid orderId, CancellationToken ct);
    Task<OrderActionResult> TryRejectAsync(Guid customerId, Guid orderId, CancellationToken ct);

    /// <summary>Accepted → Preparing. No IFoodPlatformClient preparing hook yet; persists local status after the usual active-connection resolution.</summary>
    Task<OrderActionResult> MarkPreparingAsync(Guid customerId, Guid orderId, CancellationToken ct);

    /// <summary>Preparing → ReadyForPickup via IFoodPlatformClient.MarkInvoicedAsync.</summary>
    Task<OrderActionResult> MarkReadyForPickupAsync(Guid customerId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// User command ReadyForPickup → OnTheWay. Refused with <see cref="Wasla.Application.Orders.OrderDeliveryPolicy.UserPickupNotAllowedKey"/>
    /// for platforms whose courier reports pickup (all current platforms); provider sync applies OnTheWay instead.
    /// </summary>
    Task<OrderActionResult> MarkOnTheWayAsync(Guid customerId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// User command OnTheWay → Delivered. Refused with <see cref="Wasla.Application.Orders.OrderDeliveryPolicy.UserDeliveryNotAllowedKey"/>
    /// for platforms whose courier reports delivery (all current platforms); provider sync applies Delivered instead.
    /// </summary>
    Task<OrderActionResult> MarkDeliveredAsync(Guid customerId, Guid orderId, CancellationToken ct);
}

