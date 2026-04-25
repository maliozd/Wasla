using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Orders;

public interface IOrderReadService
{
    Task<OrderListResult> GetListAsync(
        Guid customerId,
        FoodPlatform? platform,
        OrderStatus? status,
        DateTime? startDateUtc,
        DateTime? endDateUtc,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);
}

