using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Orders;
using OrderHub.Application.Time;
using OrderHub.Domain.Enums;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.Orders;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;
using Microsoft.Extensions.Localization;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("orders")]
public sealed class OrdersController : BaseController
{
    private readonly ICurrentCustomerService _currentCustomer;
    private readonly IOrderReadService _orders;
    private readonly IOrderActionService _actions;
    private readonly IOrderSyncSettingsService _orderSyncSettings;
    private readonly ILogger<OrdersController> _logger;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public OrdersController(
        ICurrentCustomerService currentCustomer,
        IOrderReadService orders,
        IOrderActionService actions,
        IOrderSyncSettingsService orderSyncSettings,
        ILogger<OrdersController> logger,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentCustomer = currentCustomer;
        _orders = orders;
        _actions = actions;
        _orderSyncSettings = orderSyncSettings;
        _logger = logger;
        _localizer = localizer;
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

        var vm = await BuildOrderListViewModelAsync(
            customer.Id, platform, status, startDate, endDate, sortBy, sortDirection, page, pageSize,
            addDateValidationErrors: true, logDateFilterAs: "Index", ct);

        return View("Index", vm);
    }

    [HttpGet("sync-settings")]
    public async Task<IActionResult> GetOrderSyncSettings(CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var r = await _orderSyncSettings.GetAsync(customer.Id, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("sync-settings")]
    public async Task<IActionResult> UpdateOrderSyncSettings([FromForm] bool enabled, CancellationToken ct = default)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var r = await _orderSyncSettings.UpdateAsync(customer.Id, enabled, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
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

        var vm = await BuildOrderListViewModelAsync(
            customer.Id, platform, status, startDate, endDate, sortBy, sortDirection, page, pageSize,
            addDateValidationErrors: false, logDateFilterAs: null, ct);

        return PartialView("_OrdersTable", vm);
    }

    /// <summary>
    /// Shared list query/projection for the Orders index page and the polling partial.
    /// Both endpoints must return the same data shape; only validation/logging differ.
    /// </summary>
    private async Task<OrderListViewModel> BuildOrderListViewModelAsync(
        Guid customerId,
        FoodPlatform? platform,
        OrderStatus? status,
        string? startDate,
        string? endDate,
        string? sortBy,
        string? sortDirection,
        int page,
        int pageSize,
        bool addDateValidationErrors,
        string? logDateFilterAs,
        CancellationToken ct)
    {
        var (safePage, safePageSize) = NormalizePaging(page, pageSize);
        DefaultTodayIfNoDates(ref startDate, ref endDate);

        var (startUtc, endUtc, startDateParsed, endDateParsed) = ParseDateFilters(startDate, endDate);

        if (addDateValidationErrors)
        {
            if (startUtc is null && !string.IsNullOrWhiteSpace(startDate))
                ModelState.AddModelError("startDate", _localizer["Orders.InvalidStartDate"].Value);
            if (endUtc is null && !string.IsNullOrWhiteSpace(endDate))
                ModelState.AddModelError("endDate", _localizer["Orders.InvalidEndDate"].Value);
        }

        var result = await _orders.GetListAsync(
            customerId,
            platform,
            status,
            startUtc,
            endUtc,
            sortBy,
            sortDirection,
            safePage,
            safePageSize,
            ct);

        if (logDateFilterAs is not null)
        {
            LogDateFilter(logDateFilterAs, startDate, endDate, startDateParsed, endDateParsed, startUtc, endUtc, result.TotalCount);
        }

        var turkeyToday = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();

        return new OrderListViewModel
        {
            TotalCount = result.TotalCount,
            TurkeyLocalToday = turkeyToday,
            UseSimpleNoOrdersMessage = !platform.HasValue && !status.HasValue
                && startDateParsed == turkeyToday
                && endDateParsed == turkeyToday
                && startDateParsed == endDateParsed,
            Orders = MapOrderRows(result.Items, tz),
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

    [HttpPost("{id:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.TryApproveAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: true) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: true) });
    }

    [HttpPost("{id:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.TryRejectAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: false) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: false) });
    }

    [HttpPost("{id:guid}/start-preparing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartPreparing(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.MarkPreparingAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/mark-ready")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.MarkReadyForPickupAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/hand-to-courier")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HandToCourier(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.MarkOnTheWayAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/mark-delivered")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkDelivered(Guid id, CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentCustomer;
        if (customer is null) return NotFound();

        var result = await _actions.MarkDeliveredAsync(customer.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    /// <summary>Maps backend <c>Orders.*</c> message keys to client dictionary keys used by <c>orders-actions.js</c>.</summary>
    private static string MapOrderActionClientMessageKey(string messageKey, bool? isApprove)
    {
        return messageKey switch
        {
            "Orders.InvalidStatusForAction" => "ordersInvalidStatusForAction",
            "Orders.ActionFailed" => "ordersActionFailed",
            "Orders.OrderActionFailed" => "ordersOrderActionFailed",
            "Orders.ApproveFailed" => "ordersApproveFailed",
            "Orders.RejectFailed" => "ordersRejectFailed",
            "Orders.ApproveSuccess" => "ordersApproveSuccess",
            "Orders.RejectSuccess" => "ordersRejectSuccess",
            "Orders.StartPreparingSuccess" => "ordersStartPreparingSuccess",
            "Orders.MarkReadySuccess" => "ordersMarkReadySuccess",
            "Orders.HandToCourierSuccess" => "ordersHandToCourierSuccess",
            "Orders.MarkDeliveredSuccess" => "ordersMarkDeliveredSuccess",
            _ => isApprove == true ? "ordersApproveFailed"
                : isApprove == false ? "ordersRejectFailed"
                : "ordersOrderActionFailed"
        };
    }

    private static List<OrderListViewModel.Row> MapOrderRows(
        IReadOnlyList<OrderListResult.Row> items,
        TimeZoneInfo timeZone) =>
        items.Select(o => new OrderListViewModel.Row
        {
            Id = o.Id,
            Platform = o.Platform,
            ExternalOrderCode = o.ExternalOrderCode,
            CustomerName = o.CustomerName,
            TotalAmount = o.TotalAmount,
            Status = o.Status,
            ReceivedAtUtc = o.ReceivedAtUtc,
            ReceivedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(o.ReceivedAtUtc, DateTimeKind.Utc),
                timeZone)
        }).ToList();

    private void LogDateFilter(
        string action,
        string? rawStart,
        string? rawEnd,
        DateOnly? startDateParsed,
        DateOnly? endDateParsed,
        DateTime? utcStart,
        DateTime? utcEndExclusive,
        int? resultCount = null)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var localStart = startDateParsed?.ToDateTime(TimeOnly.MinValue);
        var localEndExclusive = endDateParsed?.AddDays(1).ToDateTime(TimeOnly.MinValue);

        _logger.LogInformation(
            "Orders {Action} date filter: rawStart={RawStart} rawEnd={RawEnd} timeZoneId={TimeZone} localStartDate={StartDate} localEndDate={EndDate} localStart={LocalStart} localEndExclusive={LocalEndEx} utcStartInclusive={UtcStart} utcEndExclusive={UtcEnd} resultCount={ResultCount}",
            action,
            rawStart,
            rawEnd,
            tz.Id,
            startDateParsed,
            endDateParsed,
            localStart,
            localEndExclusive,
            utcStart,
            utcEndExclusive,
            resultCount);
    }

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var p = page < 1 ? 1 : page;
        var ps = pageSize is < 5 or > 100 ? 25 : pageSize;
        return (p, ps);
    }

    /// <summary>When both query params are missing, use today's Turkey local calendar date for start and end.</summary>
    private static void DefaultTodayIfNoDates(ref string? startDate, ref string? endDate)
    {
        if (string.IsNullOrWhiteSpace(startDate) && string.IsNullOrWhiteSpace(endDate))
        {
            var y = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
            var s = y.ToString("yyyy-MM-dd");
            startDate = s;
            endDate = s;
        }
    }

    private static (DateTime? StartUtc, DateTime? EndUtc, DateOnly? StartDate, DateOnly? EndDate) ParseDateFilters(
        string? startDate, string? endDate)
    {
        var (startUtc, endUtc) = OrdersReceivedAtQueryRange.FromWebQueryStrings(startDate, endDate, out var start, out var end);
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
