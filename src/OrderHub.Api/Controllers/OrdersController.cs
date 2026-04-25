using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Contracts.Enums;
using OrderHub.Contracts.Orders;
using FoodPlatformDomain = OrderHub.Domain.Enums.FoodPlatform;
using OrderStatusDomain = OrderHub.Domain.Enums.OrderStatus;
using ContractOrderDetailDto = OrderHub.Contracts.Orders.OrderDetailDto;

namespace OrderHub.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IOrderReadService _orders;

    public OrdersController(ICurrentCustomerService currentCustomer, IOrderReadService orders)
    {
        _currentCustomer = currentCustomer;
        _orders = orders;
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

        var result = await _orders.GetListAsync(
            customer.Id,
            query.Platform.HasValue ? (FoodPlatformDomain?)(int)query.Platform.Value : null,
            query.Status.HasValue ? (OrderStatusDomain?)(int)query.Status.Value : null,
            query.StartDate,
            query.EndDate,
            page,
            pageSize,
            ct);

        var items = result.Items.Select(o => new OrderListItemDto(
            o.Id,
            (FoodPlatformDto)(int)o.Platform,
            o.ExternalOrderId,
            o.CustomerName,
            o.TotalAmount,
            (OrderStatusDto)(int)o.Status,
            o.PlatformStatus,
            o.CreatedAtPlatformUtc,
            o.ReceivedAtUtc)).ToList();

        return Ok(new OrderListResponse(items, result.TotalCount, result.Page, result.PageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContractOrderDetailDto>> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound("Customer not found");

        var order = await _orders.GetByIdAsync(customer.Id, id, ct);
        if (order is null) return NotFound();

        var items = order.Items.Select(i => new OrderItemDto(
            i.Id,
            i.ProductName,
            i.Quantity,
            i.UnitPrice,
            i.TotalPrice,
            i.Notes,
            i.Options.Select(o => new OrderItemOptionDto(o.Id, o.Name, o.Price)).ToList()
        )).ToList();

        return Ok(new ContractOrderDetailDto(
            order.Id,
            (FoodPlatformDto)(int)order.Platform,
            order.ExternalOrderId,
            order.CustomerName,
            order.TotalAmount,
            order.DeliveryFee,
            order.ServiceFee,
            order.PaymentMethod.ToString(),
            order.PaymentStatus.ToString(),
            (OrderStatusDto)(int)order.Status,
            order.PlatformStatus,
            order.CreatedAtPlatformUtc,
            order.ReceivedAtUtc,
            order.AcceptedAtUtc,
            order.DeliveredAtUtc,
            order.CancelledAtUtc,
            items));
    }
}

