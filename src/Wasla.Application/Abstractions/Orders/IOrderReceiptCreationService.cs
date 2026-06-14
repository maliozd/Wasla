namespace Wasla.Application.Abstractions.Orders;

/// <summary>
/// Creates receipt print jobs when order acceptance triggers receipt creation (timing = OnAccepted).
/// </summary>
public interface IOrderReceiptCreationService
{
    Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct);
}
