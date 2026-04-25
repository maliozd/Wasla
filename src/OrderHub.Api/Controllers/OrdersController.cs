using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Contracts.Enums;
using OrderHub.Contracts.Orders;
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
    public async Task<ActionResult<OrderListResponse>> GetList(
        [FromQuery] OrderListQuery query,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        await using var db = await _customerDbFactory.CreateAsync(customer.Id, ct);

        var q = db.Orders.AsNoTracking().AsQueryable();

        if (query.Platform.HasValue) q = q.Where(o => (int)o.Platform == (int)query.Platform.Value);
        if (query.Status.HasValue) q = q.Where(o => (int)o.InternalStatus == (int)query.Status.Value);
        if (query.StartDate.HasValue) q = q.Where(o => o.CreatedAtPlatform >= query.StartDate.Value);
        if (query.EndDate.HasValue) q = q.Where(o => o.CreatedAtPlatform <= query.EndDate.Value);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(o => o.CreatedAtPlatform)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrderListItemDto(
                o.Id,
                (FoodPlatformDto)(int)o.Platform,
                o.ExternalOrderId,
                o.CustomerName,
                o.TotalAmount,
                (OrderStatusDto)(int)o.InternalStatus,
                o.PlatformStatus,
                o.CreatedAtPlatform,
                o.ReceivedAt))
            .ToListAsync(ct);

        return Ok(new OrderListResponse(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDetailDto>> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        await using var db = await _customerDbFactory.CreateAsync(customer.Id, ct);

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order is null) return NotFound();

        var items = order.Items
            .Select(i => new OrderItemDto(
                i.Id,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                i.Options.Select(o => new OrderItemOptionDto(o.Id, o.Name, o.Price)).ToList()))
            .ToList();

        return Ok(new OrderDetailDto(
            order.Id,
            (FoodPlatformDto)(int)order.Platform,
            order.ExternalOrderId,
            order.CustomerName,
            order.TotalAmount,
            order.DeliveryFee,
            order.ServiceFee,
            order.PaymentMethod.ToString(),
            order.PaymentStatus.ToString(),
            (OrderStatusDto)(int)order.InternalStatus,
            order.PlatformStatus,
            order.CreatedAtPlatform,
            order.ReceivedAt,
            order.AcceptedAt,
            order.DeliveredAt,
            order.CancelledAt,
            items));
    }
}

