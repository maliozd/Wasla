using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderReadService : IOrderReadService
{
    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly ILogger<OrderReadService> _logger;

    public OrderReadService(ICustomerDbContextFactory dbFactory, ILogger<OrderReadService> logger)
    {
        _dbFactory = dbFactory;
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
        CancellationToken ct)
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

        q = safeSortBy switch
        {
            "platform" => safeSortDirection == "asc"
                ? q.OrderBy(o => o.Platform)
                : q.OrderByDescending(o => o.Platform),
            "status" => safeSortDirection == "asc"
                ? q.OrderBy(o => o.InternalStatus)
                : q.OrderByDescending(o => o.InternalStatus),
            "customerName" => safeSortDirection == "asc"
                ? q.OrderBy(o => o.CustomerName)
                : q.OrderByDescending(o => o.CustomerName),
            "totalAmount" => safeSortDirection == "asc"
                ? q.OrderBy(o => o.TotalAmount)
                : q.OrderByDescending(o => o.TotalAmount),
            "receivedAt" => safeSortDirection == "asc"
                ? q.OrderBy(o => o.ReceivedAt)
                : q.OrderByDescending(o => o.ReceivedAt),
            _ => safeSortDirection == "asc"
                ? q.OrderBy(o => o.ReceivedAt)
                : q.OrderByDescending(o => o.ReceivedAt)
        };

        var total = await q.CountAsync(ct);

        if (_logger.IsEnabled(LogLevel.Debug) && (startDateUtc.HasValue || endDateUtc.HasValue))
        {
            _logger.LogDebug(
                "Orders list UTC filter result: startInclusive={StartUtc} endExclusive={EndExclusiveUtc} totalCount={Count}",
                startDateUtc, endDateUtc, total);
        }

        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
                o.ReceivedAt))
            .ToListAsync(ct);

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
}

