using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public interface IOrderAutoApproveService
{
    /// <summary>
    /// Applies tenant auto-approve and optional receipt print job for a newly inserted order.
    /// No-op when settings are disabled or the order is not eligible.
    /// </summary>
    Task ProcessNewlyInsertedOrderAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus insertedStatus,
        CancellationToken ct);
}
