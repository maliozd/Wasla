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

    /// <summary>
    /// Read-only operational snapshot for Live Screen.
    /// Includes every active order and Delivered orders whose DeliveredAt is inside the last two minutes.
    /// Delivered orders with no DeliveredAt are omitted; a completion time is not invented.
    /// Today and cancelled counts are Turkey-local-day aggregates and are not loaded into <c>Orders</c>.
    /// </summary>
    Task<LiveScreenSnapshotResult> GetLiveScreenSnapshotAsync(Guid customerId, CancellationToken ct);

    /// <summary>
    /// How many orders arrived at or after <paramref name="sinceUtc"/>, by the canonical <c>ReceivedAt</c>, in any
    /// status. Practice orders are not orders and never count. Only the number is read.
    /// </summary>
    Task<int> CountReceivedSinceAsync(Guid customerId, DateTime sinceUtc, CancellationToken ct);
}

