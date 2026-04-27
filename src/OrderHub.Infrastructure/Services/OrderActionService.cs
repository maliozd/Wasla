using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderActionService : IOrderActionService
{
    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly ILogger<OrderActionService> _logger;

    public OrderActionService(ICustomerDbContextFactory dbFactory, ILogger<OrderActionService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<bool> TryUpdateStatusAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus newStatus,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return false;

        // Only allow actions while still New to avoid clobbering worker-driven updates.
        if (order.InternalStatus != OrderStatus.New) return false;

        order.InternalStatus = newStatus;
        if (newStatus == OrderStatus.Accepted) order.AcceptedAt ??= DateTime.UtcNow;
        if (newStatus == OrderStatus.Cancelled) order.CancelledAt ??= DateTime.UtcNow;

        var rows = await db.SaveChangesAsync(ct);
        _logger.LogInformation("Order action updated status: orderId={OrderId} newStatus={NewStatus} rows={Rows}", orderId, newStatus, rows);
        return rows > 0;
    }
}

