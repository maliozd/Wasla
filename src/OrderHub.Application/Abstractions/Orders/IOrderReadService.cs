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
        string? sortBy,
        string? sortDirection,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);

    Task<DateTime?> GetLatestReceivedAtUtcAsync(Guid customerId, CancellationToken ct);

    Task<NewOrdersCheckResult> GetNewOrdersSinceAsync(Guid customerId, DateTime sinceReceivedAtUtc, CancellationToken ct);
}

