using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class OrderReadService : IOrderReadService
{
    private static readonly TimeSpan RecentDeliveredWindow = LiveScreenVisibility.RecentDeliveredWindow;
    private static readonly OrderStatus[] ActiveStatuses =
    [
        OrderStatus.New,
        OrderStatus.Accepted,
        OrderStatus.Preparing,
        OrderStatus.ReadyForPickup,
        OrderStatus.OnTheWay
    ];

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderReadService> _logger;

    public OrderReadService(
        ITenantDbContextFactory dbFactory,
        TimeProvider timeProvider,
        ILogger<OrderReadService> logger)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<OrderListResult> GetListAsync(
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
        bool includeLineItems = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var q = db.Orders.AsNoTracking().AsQueryable();

        if (platform.HasValue) q = q.Where(o => o.Platform == platform.Value);
        if (status.HasValue) q = q.Where(o => o.InternalStatus == status.Value);
        if (startDateUtc.HasValue) q = q.Where(o => o.ReceivedAt >= startDateUtc.Value);
        if (endDateUtc.HasValue) q = q.Where(o => o.ReceivedAt < endDateUtc.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(o =>
                o.ExternalOrderCode.Contains(term) ||
                o.ExternalOrderId.Contains(term) ||
                o.CustomerName.Contains(term) ||
                (o.CustomerPhone != null && o.CustomerPhone.Contains(term)));
        }

        var safeSortBy = string.IsNullOrWhiteSpace(sortBy) ? "receivedAt" : sortBy.Trim();
        var safeSortDirection = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

        var ascending = safeSortDirection == "asc";
        var ordered = safeSortBy switch
        {
            "platform" => ascending ? q.OrderBy(o => o.Platform) : q.OrderByDescending(o => o.Platform),
            "status" => ascending ? q.OrderBy(o => o.InternalStatus) : q.OrderByDescending(o => o.InternalStatus),
            "customerName" => ascending ? q.OrderBy(o => o.CustomerName) : q.OrderByDescending(o => o.CustomerName),
            "totalAmount" => ascending ? q.OrderBy(o => o.TotalAmount) : q.OrderByDescending(o => o.TotalAmount),
            _ => ascending ? q.OrderBy(o => o.ReceivedAt) : q.OrderByDescending(o => o.ReceivedAt)
        };

        // Rows that tie on the sorted column (same platform, status, customer or amount) need a fixed order,
        // or a row could appear on two pages or on none. Newest first, then the unique ID.
        q = ordered.ThenByDescending(o => o.ReceivedAt).ThenBy(o => o.Id);

        var total = await q.CountAsync(ct);

        if (_logger.IsEnabled(LogLevel.Debug) && (startDateUtc.HasValue || endDateUtc.HasValue))
        {
            _logger.LogDebug(
                "Orders list UTC filter result: startInclusive={StartUtc} endExclusive={EndExclusiveUtc} totalCount={Count}",
                startDateUtc, endDateUtc, total);
        }

        var pageQuery = q.Skip((page - 1) * pageSize).Take(pageSize);

        // Single SQL projection either way — never 1+N. Line items only when Live Screen asks for them.
        List<OrderListResult.Row> items;
        if (includeLineItems)
        {
            items = await pageQuery
                .Select(o => new OrderListResult.Row(
                    o.Id,
                    o.Platform,
                    o.ExternalOrderId,
                    o.ExternalOrderCode,
                    o.CustomerName,
                    o.TotalAmount,
                    o.InternalStatus,
                    o.PlatformStatus,
                    o.CreatedAtPlatform,
                    o.ReceivedAt,
                    o.Items.Count,
                    o.Items.OrderBy(i => i.Id).Select(i => i.ProductName).FirstOrDefault(),
                    o.Items
                        .OrderBy(i => i.Id)
                        .Select(i => new OrderListResult.LineItem(i.ProductName, i.Quantity, i.Notes))
                        .ToList()))
                .ToListAsync(ct);
        }
        else
        {
            items = await pageQuery
                .Select(o => new OrderListResult.Row(
                    o.Id,
                    o.Platform,
                    o.ExternalOrderId,
                    o.ExternalOrderCode,
                    o.CustomerName,
                    o.TotalAmount,
                    o.InternalStatus,
                    o.PlatformStatus,
                    o.CreatedAtPlatform,
                    o.ReceivedAt,
                    o.Items.Count,
                    o.Items.OrderBy(i => i.Id).Select(i => i.ProductName).FirstOrDefault(),
                    Array.Empty<OrderListResult.LineItem>()))
                .ToListAsync(ct);
        }

        return new OrderListResult
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<OrderDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order is null) return null;

        return new OrderDetailResult
        {
            Id = order.Id,
            Platform = order.Platform,
            ExternalOrderId = order.ExternalOrderId,
            ExternalOrderCode = order.ExternalOrderCode,
            CustomerName = order.CustomerName,
            CustomerPhone = order.CustomerPhone,
            CustomerAddress = order.CustomerAddress,
            CustomerNote = string.IsNullOrWhiteSpace(order.CustomerNote) ? null : order.CustomerNote.Trim(),
            TotalAmount = order.TotalAmount,
            DeliveryFee = order.DeliveryFee,
            ServiceFee = order.ServiceFee,
            PaymentMethod = order.PaymentMethod,
            PaymentStatus = order.PaymentStatus,
            Status = order.InternalStatus,
            PlatformStatus = order.PlatformStatus,
            CreatedAtPlatformUtc = order.CreatedAtPlatform,
            ReceivedAtUtc = order.ReceivedAt,
            AcceptedAtUtc = order.AcceptedAt,
            DeliveredAtUtc = order.DeliveredAt,
            CancelledAtUtc = order.CancelledAt,
            Items = order.Items.Select(i => new OrderDetailResult.ItemResult(
                i.Id,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                i.Options.Select(o => new OrderDetailResult.OptionResult(o.Id, o.Name, o.Price)).ToList()
            )).ToList()
        };
    }

    public async Task<LiveScreenSnapshotResult> GetLiveScreenSnapshotAsync(Guid customerId, CancellationToken ct)
    {
        var serverTimeUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStartUtc = serverTimeUtc - RecentDeliveredWindow;

        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        // One projection. Delivered rows with a null DeliveredAt are omitted: the current
        // transition writers set that timestamp when they enter Delivered, and this read
        // does not invent a completion time for rows that are already missing it.
        var rows = await db.Orders.AsNoTracking()
            .Where(o =>
                ActiveStatuses.Contains(o.InternalStatus)
                || (o.InternalStatus == OrderStatus.Delivered
                    && o.DeliveredAt != null
                    && o.DeliveredAt >= windowStartUtc
                    && o.DeliveredAt <= serverTimeUtc))
            .OrderBy(o => o.InternalStatus)
            .ThenByDescending(o => o.ReceivedAt)
            .ThenBy(o => o.Id)
            .Select(o => new
            {
                o.Id,
                o.ExternalOrderCode,
                o.Platform,
                o.InternalStatus,
                o.ReceivedAt,
                o.DeliveredAt,
                o.CustomerName,
                o.CustomerAddress,
                o.CustomerNote,
                o.TotalAmount,
                Items = o.Items
                    .OrderBy(i => i.Id)
                    .Select(i => new LiveScreenLineItemDto(i.ProductName, i.Quantity, i.Notes))
                    .ToList()
            })
            .ToListAsync(ct);

        var orders = rows.Select(o => new LiveScreenOrderDto(
            o.Id,
            o.ExternalOrderCode,
            o.Platform,
            o.InternalStatus,
            SpecifyUtc(o.ReceivedAt),
            o.DeliveredAt is null ? null : SpecifyUtc(o.DeliveredAt.Value),
            o.CustomerName,
            o.CustomerAddress ?? string.Empty,
            string.IsNullOrWhiteSpace(o.CustomerNote) ? null : o.CustomerNote.Trim(),
            o.TotalAmount,
            o.Items)).ToList();

        var utcServer = SpecifyUtc(serverTimeUtc);
        var turkeyToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(utcServer, TimeZoneHelper.ResolveTurkeyTimeZone()));
        OrdersReceivedAtQueryRange.MapLocalDatesToUtcRange(turkeyToday, turkeyToday, out var todayStartUtc, out var todayEndUtc);
        var todayStart = todayStartUtc ?? throw new InvalidOperationException("Turkey local day did not map to a UTC start.");
        var todayEnd = todayEndUtc ?? throw new InvalidOperationException("Turkey local day did not map to a UTC end.");
        var receivedToday = db.Orders.AsNoTracking()
            .Where(o => o.ReceivedAt >= todayStart && o.ReceivedAt < todayEnd);
        var todayOrderCount = await receivedToday.CountAsync(ct);
        var cancelledOrderCount = await receivedToday.CountAsync(o => o.InternalStatus == OrderStatus.Cancelled, ct);

        return new LiveScreenSnapshotResult(utcServer, orders, todayOrderCount, cancelledOrderCount);
    }

    public async Task<int> CountReceivedSinceAsync(Guid customerId, DateTime sinceUtc, CancellationToken ct)
    {
        var since = sinceUtc.Kind == DateTimeKind.Local ? sinceUtc.ToUniversalTime() : SpecifyUtc(sinceUtc);
        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        return await db.Orders.AsNoTracking().CountAsync(o => o.ReceivedAt >= since, ct);
    }

    private static DateTime SpecifyUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

