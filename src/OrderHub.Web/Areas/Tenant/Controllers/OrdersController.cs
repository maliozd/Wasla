using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Domain.Enums;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.Orders;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
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
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? sortBy = "receivedAt",
        [FromQuery] string? sortDirection = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var (safePage, safePageSize) = NormalizePaging(page, pageSize);

        var (startUtc, endUtc, startDateParsed, endDateParsed) = ParseDateFilters(startDate, endDate);
        if (startUtc is null && !string.IsNullOrWhiteSpace(startDate))
            ModelState.AddModelError("startDate", "Başlangıç tarihi geçersiz.");
        if (endUtc is null && !string.IsNullOrWhiteSpace(endDate))
            ModelState.AddModelError("endDate", "Bitiş tarihi geçersiz.");

        var result = await _orders.GetListAsync(
            customer.Id,
            platform,
            status,
            startUtc,
            endUtc,
            sortBy,
            sortDirection,
            safePage,
            safePageSize,
            ct);

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
                StartDate = startDateParsed,
                EndDate = endDateParsed,
                SortBy = NormalizeSortBy(sortBy),
                SortDirection = NormalizeSortDirection(sortDirection),
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
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? sortBy = "receivedAt",
        [FromQuery] string? sortDirection = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var (safePage, safePageSize) = NormalizePaging(page, pageSize);
        var (startUtc, endUtc, startDateParsed, endDateParsed) = ParseDateFilters(startDate, endDate);
        var result = await _orders.GetListAsync(
            customer.Id,
            platform,
            status,
            startUtc,
            endUtc,
            sortBy,
            sortDirection,
            safePage,
            safePageSize,
            ct);

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
                StartDate = startDateParsed,
                EndDate = endDateParsed,
                SortBy = NormalizeSortBy(sortBy),
                SortDirection = NormalizeSortDirection(sortDirection),
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

    private static (DateTime? StartUtc, DateTime? EndUtc, DateOnly? StartDate, DateOnly? EndDate) ParseDateFilters(string? startDate, string? endDate)
    {
        DateOnly? start = null;
        DateOnly? end = null;

        if (!string.IsNullOrWhiteSpace(startDate) && DateOnly.TryParse(startDate, out var s))
            start = s;
        if (!string.IsNullOrWhiteSpace(endDate) && DateOnly.TryParse(endDate, out var e))
            end = e;

        DateTime? startUtc = start.HasValue
            ? DateTime.SpecifyKind(start.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : null;

        DateTime? endUtc = end.HasValue
            ? DateTime.SpecifyKind(end.Value.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc)
            : null;

        return (startUtc, endUtc, start, end);
    }

    private static string NormalizeSortDirection(string? sortDirection) =>
        string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";

    private static string NormalizeSortBy(string? sortBy)
    {
        var s = string.IsNullOrWhiteSpace(sortBy) ? "receivedAt" : sortBy.Trim();
        return s switch
        {
            "receivedAt" or "platform" or "status" or "customerName" or "totalAmount" => s,
            _ => "receivedAt"
        };
    }
}

