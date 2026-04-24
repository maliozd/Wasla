using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly ICustomerDbContextFactory _customerDbFactory;

    public OrdersController(ICurrentCustomerService currentCustomer, ICustomerDbContextFactory customerDbFactory)
    {
        _currentCustomer = currentCustomer;
        _customerDbFactory = customerDbFactory;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] FoodPlatform? platform,
        [FromQuery] OrderStatus? status,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        await using var db = (CustomerDbContext)await _customerDbFactory.CreateAsync(customer.Id, ct);

        var q = db.Orders.AsNoTracking().AsQueryable();

        if (platform.HasValue) q = q.Where(o => o.Platform == platform.Value);
        if (status.HasValue) q = q.Where(o => o.InternalStatus == status.Value);
        if (startDate.HasValue) q = q.Where(o => o.CreatedAtPlatform >= startDate.Value);
        if (endDate.HasValue) q = q.Where(o => o.CreatedAtPlatform <= endDate.Value);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(o => o.CreatedAtPlatform)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.Platform,
                o.ExternalOrderId,
                o.ExternalOrderCode,
                o.InternalStatus,
                o.PlatformStatus,
                o.CustomerName,
                o.TotalAmount,
                o.DeliveryFee,
                o.PaymentMethod,
                o.PaymentStatus,
                o.CreatedAtPlatform,
                o.ReceivedAt
            })
            .ToListAsync(ct);

        return Ok(new
        {
            page,
            pageSize,
            total,
            items
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        await using var db = (CustomerDbContext)await _customerDbFactory.CreateAsync(customer.Id, ct);

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order is null) return NotFound();

        return Ok(new
        {
            order.Id,
            order.Platform,
            order.ExternalOrderId,
            order.ExternalOrderCode,
            order.InternalStatus,
            order.PlatformStatus,
            order.CustomerName,
            order.CustomerPhone,
            order.CustomerAddress,
            order.TotalAmount,
            order.DeliveryFee,
            order.ServiceFee,
            order.PaymentMethod,
            order.PaymentStatus,
            order.CreatedAtPlatform,
            order.ReceivedAt,
            order.AcceptedAt,
            order.DeliveredAt,
            order.CancelledAt,
            order.RawPayloadJson,
            items = order.Items.Select(i => new
            {
                i.Id,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                options = i.Options.Select(o => new { o.Id, o.Name, o.Price })
            })
        });
    }
}

