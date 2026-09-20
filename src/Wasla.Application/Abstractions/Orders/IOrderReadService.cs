using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public interface IOrderReadService
{
    /// <param name="startDateUtc">Inclusive lower bound on <c>ReceivedAt</c> (UTC), or null for no lower bound.</param>
    /// <param name="endDateUtc">Exclusive upper bound on <c>ReceivedAt</c> (UTC), or null for no upper bound.</param>
    /// <param name="includeLineItems">
    /// When true, projects product/qty/notes for Live Screen cards.
    /// Keep false for management list/history to avoid loading item rows unnecessarily.
    /// </param>
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
        string? search,
        CancellationToken ct,
        bool includeLineItems = false);

    Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct);
}

