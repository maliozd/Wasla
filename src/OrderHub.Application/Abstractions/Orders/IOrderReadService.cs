using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Orders;

public interface IOrderReadService
{
    /// <param name="startDateUtc">Inclusive lower bound on <c>ReceivedAt</c> (UTC), or null for no lower bound.</param>
    /// <param name="endDateUtc">Exclusive upper bound on <c>ReceivedAt</c> (UTC), or null for no upper bound.</param>
    Task<OrderListResult> GetListAsync(
        Guid customerId,
        FoodPlatform? platform,
        OrderStatus? status,
        DateTime? startDateUtc,
        DateTime? endDateUtc,
        string? sortBy,
        string? sortDirection,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);
}

