using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Orders;
using Wasla.Application.Security;
using Wasla.Contracts.Enums;
using Wasla.Contracts.Orders;
using FoodPlatformDomain = Wasla.Domain.Enums.FoodPlatform;
using OrderStatusDomain = Wasla.Domain.Enums.OrderStatus;
using ContractOrderDetailDto = Wasla.Contracts.Orders.OrderDetailDto;

namespace Wasla.Api.Controllers;

// Order list and details (read only), as on Web (OrdersController, CanViewOrders).
[ApiController]
[Route("api/orders")]
[Authorize(Policy = WaslaTenantPolicies.CanViewOrders)]
public sealed class OrdersController : ControllerBase
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IOrderReadService _orders;

    public OrdersController(ICurrentTenantService currentTenant, IOrderReadService orders)
    {
        _currentTenant = currentTenant;
        _orders = orders;
    }

    [HttpGet]
    public async Task<ActionResult<OrderListResponse>> GetList(
        [FromQuery] OrderListQuery query,
        CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var (utcStart, utcEndExclusive) = OrdersReceivedAtQueryRange.FromApiDateTimes(query.StartDate, query.EndDate);

        var result = await _orders.GetListAsync(
            tenant.Id,
            query.Platform.HasValue ? (FoodPlatformDomain?)(int)query.Platform.Value : null,
            query.Status.HasValue ? (OrderStatusDomain?)(int)query.Status.Value : null,
            utcStart,
            utcEndExclusive,
            sortBy: null,
            sortDirection: null,
            page,
            pageSize,
            search: null,
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
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var order = await _orders.GetByIdAsync(tenant.Id, id, ct);
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

