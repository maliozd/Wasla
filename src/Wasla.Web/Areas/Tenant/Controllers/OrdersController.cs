using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Orders;
using Wasla.Application.Time;
using Wasla.Domain.Enums;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Orders;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using Wasla.Web.Ui;
using Microsoft.Extensions.Localization;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]
[Route("orders")]
public sealed class OrdersController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IOrderReadService _orders;
    private readonly IOrderActionService _actions;
    private readonly IOrderSyncSettingsService _orderSyncSettings;
    private readonly ITenantOrderSettingsService _orderSettings;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly IManualOrderPrintService _manualPrint;
    private readonly IValidator<UpdateTenantOrderSettingsCommand> _orderSettingsValidator;
    private readonly ILogger<OrdersController> _logger;
    private readonly IStringLocalizer<Wasla.Web.SharedResource> _localizer;

    public OrdersController(
        ICurrentTenantService currentTenant,
        IOrderReadService orders,
        IOrderActionService actions,
        IOrderSyncSettingsService orderSyncSettings,
        ITenantOrderSettingsService orderSettings,
        IOrderReceiptCreationService receiptCreation,
        IManualOrderPrintService manualPrint,
        IValidator<UpdateTenantOrderSettingsCommand> orderSettingsValidator,
        ILogger<OrdersController> logger,
        IStringLocalizer<Wasla.Web.SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _orders = orders;
        _actions = actions;
        _orderSyncSettings = orderSyncSettings;
        _orderSettings = orderSettings;
        _receiptCreation = receiptCreation;
        _manualPrint = manualPrint;
        _orderSettingsValidator = orderSettingsValidator;
        _logger = logger;
        _localizer = localizer;
    }

    /// <summary>
    /// Orders is the management search page: current and historical lookup over the same
    /// server-side filtered/paged query. Live operational display lives on <see cref="LiveDisplay" />.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
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

        if (IsLegacyFullscreenRequest())
            return RedirectToAction(nameof(LiveDisplay));

        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, platform, status, startDate, endDate, search,
            sortBy, sortDirection, page, pageSize,
            useHistoryDefaults: false,
            addDateValidationErrors: true, logDateFilterAs: "Index", ct,
            includeLineItems: false);

        return View("Index", vm);
    }

    /// <summary>Legacy Order History route: Orders now owns historical lookup, so keep old links working.</summary>
    [HttpGet("history")]
    public IActionResult History(
        [FromQuery] FoodPlatform? platform,
        [FromQuery] OrderStatus? status,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDirection,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        var preserved = new List<KeyValuePair<string, string?>>
        {
            new("platform", platform?.ToString()),
            new("status", status?.ToString()),
            new("startDate", startDate),
            new("endDate", endDate),
            new("search", search),
            new("sortBy", sortBy),
            new("sortDirection", sortDirection),
            new("page", page?.ToString()),
            new("pageSize", pageSize?.ToString())
        };

        var query = preserved
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!.Trim())}")
            .ToList();

        return Redirect(query.Count == 0 ? "/orders" : "/orders?" + string.Join("&", query));
    }

    [HttpGet("sync-settings")]
    [Authorize(Policy = TenantPolicies.TenantManagerOrOwner)]
    public async Task<IActionResult> GetOrderSyncSettings(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var r = await _orderSyncSettings.GetAsync(tenant.Id, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("sync-settings")]
    [Authorize(Policy = TenantPolicies.TenantManagerOrOwner)]
    public async Task<IActionResult> UpdateOrderSyncSettings([FromForm] bool enabled, CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var r = await _orderSyncSettings.UpdateAsync(tenant.Id, enabled, ct);
        return Ok(new { orderSyncEnabled = r.OrderSyncEnabled });
    }

    [HttpGet("order-settings")]
    [Authorize(Policy = TenantPolicies.TenantManagerOrOwner)]
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
    [Authorize(Policy = TenantPolicies.TenantManagerOrOwner)]
    public async Task<IActionResult> UpdateOrderSettings(
        [FromForm] bool autoApproveNewOrders,
        [FromForm] bool autoPrintReceiptOnAutoApprove,
        [FromForm] int receiptPrintCopyCount,
        CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var command = new UpdateTenantOrderSettingsCommand(
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

    /// <summary>
    /// Table fragment used to re-render Orders after a lifecycle action. It must accept the same
    /// filter set as <see cref="Index" /> so the refreshed page matches what the user is looking at.
    /// </summary>
    [HttpGet("table")]
    public async Task<IActionResult> Table(
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
            useHistoryDefaults: false,
            addDateValidationErrors: false, logDateFilterAs: null, ct,
            includeLineItems: false);

        return PartialView("_OrdersTable", vm);
    }

    /// <summary>
    /// Polling partial for Live Screen operational cards. Same query shape as LiveDisplay; dedicated markup.
    /// </summary>
    [HttpGet("live-screen")]
    [Authorize(Policy = TenantPolicies.CanViewLiveScreen)]
    public async Task<IActionResult> LiveScreenPartial(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday().ToString("yyyy-MM-dd");
        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, null, null, today, today, search: null,
            sortBy: "receivedAt", sortDirection: "desc", page: 1, pageSize: 100,
            useHistoryDefaults: false,
            addDateValidationErrors: false, logDateFilterAs: null, ct,
            includeLineItems: true);

        return PartialView("_LiveScreenOrders", vm);
    }

    [HttpGet("live-display")]
    [Authorize(Policy = TenantPolicies.CanViewLiveScreen)]
    public async Task<IActionResult> LiveDisplay(CancellationToken ct = default)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var today = OrdersReceivedAtQueryRange.GetTurkeyLocalToday().ToString("yyyy-MM-dd");
        var vm = await BuildOrderListViewModelAsync(
            tenant.Id, null, null, today, today, search: null,
            sortBy: "receivedAt", sortDirection: "desc", page: 1, pageSize: 100,
            useHistoryDefaults: false,
            addDateValidationErrors: false, logDateFilterAs: null, ct,
            includeLineItems: true);

        ViewData["CustomerName"] = tenant.Name;
        return View("LiveDisplay", vm);
    }

    /// <summary>
    /// Shared list query/projection for Orders management, Live Screen, and polling partials.
    /// Live Screen opts into line-item projection; management list/history keep the lighter shape.
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
        CancellationToken ct,
        bool includeLineItems = false)
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
            ct,
            includeLineItems);

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
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var vm = await BuildOrderDetailViewModelAsync(id, ct);
        if (vm is null) return NotFound();

        return View("Details", vm);
    }

    /// <summary>
    /// Lazy detail markup for the Live Screen modal. Same tenant-scoped read and authorization as
    /// <see cref="Details" />; only the shell differs, so lifecycle rules are not duplicated.
    /// </summary>
    [HttpGet("{id:guid}/detail-panel")]
    public async Task<IActionResult> DetailPanel(Guid id, CancellationToken ct)
    {
        var vm = await BuildOrderDetailViewModelAsync(id, ct);
        if (vm is null) return NotFound();

        return PartialView("_OrderDetailPanel", vm);
    }

    private async Task<OrderDetailViewModel?> BuildOrderDetailViewModelAsync(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return null;

        var order = await _orders.GetByIdAsync(tenant.Id, id, ct);
        if (order is null) return null;

        var printState = await _manualPrint.GetReceiptPrintStateAsync(tenant.Id, id, ct);

        var tz = TimeZoneHelper.ResolveTurkeyTimeZone();
        var receivedLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(order.ReceivedAtUtc, DateTimeKind.Utc),
            tz);
        DateTime? acceptedLocal = order.AcceptedAtUtc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(order.AcceptedAtUtc.Value, DateTimeKind.Utc),
                tz)
            : null;

        return new OrderDetailViewModel
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
            BackUrl = "/orders",
            ReceiptPrintInProgress = printState.HasActiveJob,
            ReceiptCanReprint = printState.CanReprint,
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
    }

    /// <summary>
    /// Queues a receipt for the order through the existing Print Bridge job pipeline. Physical printing
    /// is done by the desktop Print Bridge after it claims the job, so a success here means "queued".
    /// </summary>
    [HttpPost("{id:guid}/print")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TenantPolicies.CanManualPrint)]
    public async Task<IActionResult> Print(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var result = await _manualPrint.QueueReceiptPrintAsync(tenant.Id, id, tenant.Name, ct);

        var body = new
        {
            success = result.Success,
            message = _localizer[result.MessageKey].Value
        };

        return result.Outcome switch
        {
            ManualOrderPrintOutcome.OrderNotFound => NotFound(body),
            ManualOrderPrintOutcome.AlreadyQueued => Conflict(body),
            _ when !result.Success => BadRequest(body),
            _ => Ok(body)
        };
    }

    [HttpPost("{id:guid}/approve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
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
                DisplayImageUrl = OrderProductImageHelper.ResolveDisplayImageUrl(null, imageSeed),
                LineItems = o.LineItems
                    .Select(i => new OrderListViewModel.LineItem
                    {
                        ProductName = i.ProductName,
                        Quantity = i.Quantity,
                        Notes = i.Notes
                    })
                    .ToList()
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
