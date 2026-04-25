using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderReadService : IOrderReadService
{
    private readonly ICustomerDbContextFactory _dbFactory;

    public OrderReadService(ICustomerDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<OrderListResult> GetListAsync(
        Guid customerId,
        FoodPlatform? platform,
        OrderStatus? status,
        DateTime? startDateUtc,
        DateTime? endDateUtc,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        await using var db = await _dbFactory.CreateAsync(customerId, ct);
        var q = db.Orders.AsNoTracking().AsQueryable();

        if (platform.HasValue) q = q.Where(o => o.Platform == platform.Value);
        if (status.HasValue) q = q.Where(o => o.InternalStatus == status.Value);
        if (startDateUtc.HasValue) q = q.Where(o => o.CreatedAtPlatform >= startDateUtc.Value);
        if (endDateUtc.HasValue) q = q.Where(o => o.CreatedAtPlatform <= endDateUtc.Value);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(o => o.CreatedAtPlatform)
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

