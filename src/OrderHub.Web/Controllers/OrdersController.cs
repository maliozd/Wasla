using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Web.Models.Orders;

namespace OrderHub.Web.Controllers;

[Authorize]
[Route("orders")]
public sealed class OrdersController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IOrderReadService _orders;

    public OrdersController(ICurrentCustomerService currentCustomer, IOrderReadService orders)
    {
        _currentCustomer = currentCustomer;
        _orders = orders;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] FoodPlatform? platform,
        [FromQuery] OrderStatus? status,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var (safePage, safePageSize) = NormalizePaging(page, pageSize);

        var result = await _orders.GetListAsync(customer.Id, platform, status, startDate, endDate, safePage, safePageSize, ct);

        var vm = new OrderListViewModel
        {
            TotalCount = result.TotalCount,
            Orders = result.Items.Select(o => new OrderListViewModel.Row
            {
                Id = o.Id,
                Platform = o.Platform,
                ExternalOrderCode = o.ExternalOrderCode,
                CustomerName = o.CustomerName,
                TotalAmount = o.TotalAmount,
                Status = o.Status,
                ReceivedAtUtc = o.ReceivedAtUtc
            }).ToList(),
            Filters = new OrderFilterViewModel
            {
                Platform = platform,
                Status = status,
                StartDateUtc = startDate,
                EndDateUtc = endDate,
                Page = safePage,
                PageSize = safePageSize
            }
        };

        return View("Index", vm);
    }

    [HttpGet("table")]
    public async Task<IActionResult> Table(
        [FromQuery] FoodPlatform? platform,
        [FromQuery] OrderStatus? status,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var (safePage, safePageSize) = NormalizePaging(page, pageSize);
        var result = await _orders.GetListAsync(customer.Id, platform, status, startDate, endDate, safePage, safePageSize, ct);

        var vm = new OrderListViewModel
        {
            TotalCount = result.TotalCount,
            Orders = result.Items.Select(o => new OrderListViewModel.Row
            {
                Id = o.Id,
                Platform = o.Platform,
                ExternalOrderCode = o.ExternalOrderCode,
                CustomerName = o.CustomerName,
                TotalAmount = o.TotalAmount,
                Status = o.Status,
                ReceivedAtUtc = o.ReceivedAtUtc
            }).ToList(),
            Filters = new OrderFilterViewModel
            {
                Platform = platform,
                Status = status,
                StartDateUtc = startDate,
                EndDateUtc = endDate,
                Page = safePage,
                PageSize = safePageSize
            }
        };

        return PartialView("_OrdersTable", vm);
    }

    [HttpGet("details/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var order = await _orders.GetByIdAsync(customer.Id, id, ct);
        if (order is null) return NotFound();

        var vm = new OrderDetailViewModel
        {
            Id = order.Id,
            Platform = order.Platform,
            ExternalOrderId = order.ExternalOrderId,
            ExternalOrderCode = order.ExternalOrderCode,
            Status = order.Status,
            CustomerName = order.CustomerName,
            CustomerPhone = order.CustomerPhone,
            CustomerAddress = order.CustomerAddress,
            TotalAmount = order.TotalAmount,
            DeliveryFee = order.DeliveryFee,
            ServiceFee = order.ServiceFee,
            CreatedAtPlatformUtc = order.CreatedAtPlatformUtc,
            ReceivedAtUtc = order.ReceivedAtUtc,
            Items = order.Items.Select(i => new OrderDetailViewModel.ItemRow
            {
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                TotalPrice = i.TotalPrice,
                Notes = i.Notes,
                Options = i.Options.Select(o => new OrderDetailViewModel.OptionRow
                {
                    Name = o.Name,
                    Price = o.Price
                }).ToList()
            }).ToList()
        };

        return View("Details", vm);
    }

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var p = page < 1 ? 1 : page;
        var ps = pageSize is < 5 or > 100 ? 25 : pageSize;
        return (p, ps);
    }
}

