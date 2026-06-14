using FluentValidation;
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
using OrderHub.Web.Ui;
using Microsoft.Extensions.Localization;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("orders")]
public sealed class OrdersController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IOrderReadService _orders;
    private readonly IOrderActionService _actions;
    private readonly IOrderSyncSettingsService _orderSyncSettings;
    private readonly ICustomerOrderSettingsService _orderSettings;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly IValidator<UpdateCustomerOrderSettingsCommand> _orderSettingsValidator;
    private readonly ILogger<OrdersController> _logger;
    private readonly IStringLocalizer<OrderHub.Web.SharedResource> _localizer;

    public OrdersController(
        ICurrentTenantService currentTenant,
        IOrderReadService orders,
        IOrderActionService actions,
        IOrderSyncSettingsService orderSyncSettings,
        ICustomerOrderSettingsService orderSettings,
        IOrderReceiptCreationService receiptCreation,
        IValidator<UpdateCustomerOrderSettingsCommand> orderSettingsValidator,
        ILogger<OrdersController> logger,
        IStringLocalizer<OrderHub.Web.SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _orders = orders;
        _actions = actions;
        _orderSyncSettings = orderSyncSettings;
        _orderSettings = orderSettings;
        _receiptCreation = receiptCreation;
        _orderSettingsValidator = orderSettingsValidator;
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
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (IsLegacyFullscreenRequest())
            return RedirectToAction(nameof(LiveDisplay));

        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, platform, status, startDate, endDate, search: null,
            sortBy, sortDirection, page, pageSize,
            useHistoryDefaults: false,
            addDateValidationErrors: true, logDateFilterAs: "Index", ct);

        return View("Index", vm);
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] FoodPlatform? platform,
        [FromQuery] OrderStatus? status,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? search,
        [FromQuery] string? sortBy = "receivedAt",
        [FromQuery] string? sortDirection = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, platform, status, startDate, endDate, search,
            sortBy, sortDirection, page, pageSize,
            useHistoryDefaults: true,
            addDateValidationErrors: true, logDateFilterAs: "History", ct);

        vm.ListBasePath = "/orders/history";
        vm.IsHistoryPage = true;

        return View("History", vm);
    }

    [HttpGet("sync-settings")]
    public async Task<IActionResult> GetOrderSyncSettings(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var r = await _orderSyncSettings.GetAsync(tenant.Id, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("sync-settings")]
    public async Task<IActionResult> UpdateOrderSyncSettings([FromForm] bool enabled, CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var r = await _orderSyncSettings.UpdateAsync(tenant.Id, enabled, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
    }

    [HttpGet("order-settings")]
    public async Task<IActionResult> GetOrderSettings(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var r = await _orderSettings.GetAsync(tenant.Id, ct);
        return Ok(new
        {
            autoApproveNewOrders = r.AutoApproveNewOrders,
            autoPrintReceiptOnAutoApprove = r.AutoPrintReceiptOnAutoApprove,
            receiptCreationTiming = ReceiptCreationTimingCodes.FromAutoPrintReceiptSetting(r.AutoPrintReceiptOnAutoApprove),
            receiptPrintCopyCount = r.ReceiptPrintCopyCount
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("order-settings")]
    public async Task<IActionResult> UpdateOrderSettings(
        [FromForm] bool autoApproveNewOrders,
        [FromForm] bool autoPrintReceiptOnAutoApprove,
        [FromForm] int receiptPrintCopyCount,
        CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var command = new UpdateCustomerOrderSettingsCommand(
            autoApproveNewOrders,
            autoPrintReceiptOnAutoApprove,
            receiptPrintCopyCount);

        var validation = await _orderSettingsValidator.ValidateAsync(command, ct);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors.FirstOrDefault();
            var messageKey = firstError?.ErrorMessage ?? "Orders.OrderSettingsUpdateFailed";
            var message = _localizer[messageKey].Value;
            return BadRequest(new { message });
        }

        try
        {
            var r = await _orderSettings.UpdateAsync(tenant.Id, command, ct);
            return Ok(new
            {
                autoApproveNewOrders = r.AutoApproveNewOrders,
                autoPrintReceiptOnAutoApprove = r.AutoPrintReceiptOnAutoApprove,
                receiptCreationTiming = ReceiptCreationTimingCodes.FromAutoPrintReceiptSetting(r.AutoPrintReceiptOnAutoApprove),
                receiptPrintCopyCount = r.ReceiptPrintCopyCount
            });
        }
        catch (ValidationException)
        {
            return BadRequest(new { message = _localizer["Orders.OrderSettingsUpdateFailed"].Value });
        }
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
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, platform, status, startDate, endDate, search: null,
            sortBy, sortDirection, page, pageSize,
            useHistoryDefaults: false,
            addDateValidationErrors: false, logDateFilterAs: null, ct);

        return PartialView("_OrdersTable", vm);
    }

    [HttpGet("live-display")]
    public async Task<IActionResult> LiveDisplay(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday().ToString("yyyy-MM-dd");
        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, null, null, today, today, search: null,
            sortBy: "receivedAt", sortDirection: "desc", page: 1, pageSize: 100,
            useHistoryDefaults: false,
            addDateValidationErrors: false, logDateFilterAs: null, ct);

        ViewData["CustomerName"] = tenant.Name;
        return View("LiveDisplay", vm);
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
        string? search,
        string? sortBy,
        string? sortDirection,
        int page,
        int pageSize,
        bool useHistoryDefaults,
        bool addDateValidationErrors,
        string? logDateFilterAs,
        CancellationToken ct)
    {
        var (safePage, safePageSize) = NormalizePaging(page, pageSize);
        DefaultDateRangeIfNoDates(ref startDate, ref endDate, useHistoryDefaults);

        var (startUtc, endUtc, startDateParsed, endDateParsed) = ParseDateFilters(startDate, endDate);
        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

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
            trimmedSearch,
            ct);

        if (logDateFilterAs is not null)
        {
            LogDateFilter(logDateFilterAs, startDate, endDate, startDateParsed, endDateParsed, startUtc, endUtc, result.TotalCount);
        }

        var turkeyToday = OrdersReceivedAtQueryRange.GetTurkeyLocalToday();
        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var defaultHistory = OrdersReceivedAtQueryRange.GetDefaultHistoryRange();

        return new OrderListViewModel
        {
            TotalCount = result.TotalCount,
            TurkeyLocalToday = turkeyToday,
            UseSimpleNoOrdersMessage = useHistoryDefaults
                ? !platform.HasValue && !status.HasValue && string.IsNullOrWhiteSpace(trimmedSearch)
                    && startDateParsed == defaultHistory.Start
                    && endDateParsed == defaultHistory.End
                : !platform.HasValue && !status.HasValue
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
                Search = trimmedSearch,
                SortBy = NormalizeSortBy(sortBy),
                SortDirection = NormalizeSortDirection(sortDirection),
                Page = safePage,
                PageSize = safePageSize
            }
        };
    }

    [HttpGet("details/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, [FromQuery] string? from, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var order = await _orders.GetByIdAsync(tenant.Id, id, ct);
        if (order is null) return NotFound();

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var receivedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(order.ReceivedAtUtc, DateTimeKind.Utc),
            tz);
        DateTime? acceptedLocal = order.AcceptedAtUtc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(order.AcceptedAtUtc.Value, DateTimeKind.Utc),
                tz)
            : null;

        var fromHistory = string.Equals(from, "history", StringComparison.OrdinalIgnoreCase);

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
            PaymentMethod = order.PaymentMethod,
            CreatedAtPlatformUtc = order.CreatedAtPlatformUtc,
            ReceivedAtUtc = order.ReceivedAtUtc,
            ReceivedAtLocal = receivedLocal,
            AcceptedAtUtc = order.AcceptedAtUtc,
            AcceptedAtLocal = acceptedLocal,
            BackUrl = fromHistory ? "/orders/history" : "/orders",
            BackFromHistory = fromHistory,
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
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.TryApproveAsync(tenant.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: true) });

        await _receiptCreation.TryCreateOnOrderAcceptedAsync(tenant.Id, id, ct).ConfigureAwait(false);

        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: true) });
    }

    [HttpPost("{id:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.TryRejectAsync(tenant.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: false) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: false) });
    }

    [HttpPost("{id:guid}/start-preparing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartPreparing(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.MarkPreparingAsync(tenant.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/mark-ready")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkReady(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.MarkReadyForPickupAsync(tenant.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/hand-to-courier")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HandToCourier(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.MarkOnTheWayAsync(tenant.Id, id, ct);
        if (!result.Succeeded) return BadRequest(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
        return Ok(new { message = MapOrderActionClientMessageKey(result.MessageKey, isApprove: null) });
    }

    [HttpPost("{id:guid}/mark-delivered")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkDelivered(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _actions.MarkDeliveredAsync(tenant.Id, id, ct);
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
        items.Select(o =>
        {
            var imageSeed = OrderProductImageHelper.BuildImageSeed(o.Id, o.ExternalOrderCode, o.FirstProductName);
            return new OrderListViewModel.Row
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
                    timeZone),
                ItemCount = o.ItemCount,
                FirstProductName = o.FirstProductName,
                DisplayImageUrl = OrderProductImageHelper.ResolveDisplayImageUrl(null, imageSeed)
            };
        }).ToList();

    private bool IsLegacyFullscreenRequest()
    {
        if (!Request.Query.TryGetValue("fullscreen", out var value))
            return false;

        var v = value.ToString().Trim();
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

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

    private static void DefaultDateRangeIfNoDates(ref string? startDate, ref string? endDate, bool useHistoryDefaults)
    {
        if (!string.IsNullOrWhiteSpace(startDate) || !string.IsNullOrWhiteSpace(endDate))
            return;

        if (useHistoryDefaults)
        {
            var (historyStart, historyEnd) = OrdersReceivedAtQueryRange.GetDefaultHistoryRange();
            startDate = historyStart.ToString("yyyy-MM-dd");
            endDate = historyEnd.ToString("yyyy-MM-dd");
        }
        else
        {
            DefaultTodayIfNoDates(ref startDate, ref endDate);
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
