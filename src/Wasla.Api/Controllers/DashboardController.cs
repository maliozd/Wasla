using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Reports;
using Wasla.Application.Security;
using Wasla.Application.Time;
using Wasla.Contracts.Dashboard;
using Wasla.Contracts.Enums;
using Wasla.Contracts.Orders;

namespace Wasla.Api.Controllers;

// Reports, as on Web (DashboardController, CanViewReports). Every figure comes from the shared order report
// (IOrderReportService, docs/orders/reporting.md); this controller only maps it.
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = WaslaTenantPolicies.CanViewReports)]
public sealed class DashboardController : ControllerBase
{
    private const int TodayRecentOrderCount = 5;

    private readonly ICurrentTenantService _currentTenant;
    private readonly IDashboardService _dashboard;
    private readonly IOrderReportService _reports;
    private readonly TimeProvider _time;

    public DashboardController(
        ICurrentTenantService currentTenant,
        IDashboardService dashboard,
        IOrderReportService reports,
        TimeProvider time)
    {
        _currentTenant = currentTenant;
        _dashboard = dashboard;
        _reports = reports;
        _time = time;
    }

    /// <summary>Today's figures. <c>revenue</c> is the value of every order placed today, in any status.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var today = await _dashboard.GetTodayAsync(tenant.Id, ct);

        return Ok(new
        {
            todayOrderCount = today.TodayOrderCount,
            revenue = today.TodayOrderValue,
            pendingCount = today.ActiveOrderCount,
            platformBreakdown = today.PlatformSummary
                .Select(p => new { platform = p.Platform, count = p.Count, revenue = p.OrderValue }),
            recentOrders = today.RecentOrders
                .Select(o => new
                {
                    o.Id,
                    o.Platform,
                    o.ExternalOrderCode,
                    InternalStatus = o.Status,
                    o.CustomerName,
                    o.TotalAmount,
                    CreatedAtPlatform = o.CreatedAtPlatformUtc,
                    OrderedAt = o.OrderedAtUtc
                })
        });
    }

    /// <summary>Today's figures and synchronization runs. <c>TodayRevenue</c> is the value of every order placed today, in any status.</summary>
    [HttpGet("today")]
    public async Task<ActionResult<DashboardSummaryDto>> Today(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var today = await _dashboard.GetTodayAsync(tenant.Id, ct);
        var sync = await _dashboard.GetTodaySyncStatisticsAsync(tenant.Id, ct);

        return Ok(new DashboardSummaryDto(
            today.TodayOrderCount,
            today.TodayOrderValue,
            sync.SuccessCount,
            sync.TotalCount,
            sync.SuccessRate,
            today.PlatformSummary
                .Select(p => new PlatformBreakdownDto((FoodPlatformDto)(int)p.Platform, p.Count, p.OrderValue))
                .ToList(),
            today.RecentOrders
                .Take(TodayRecentOrderCount)
                .Select(o => new OrderListItemDto(
                    o.Id,
                    (FoodPlatformDto)(int)o.Platform,
                    o.ExternalOrderId,
                    o.CustomerName,
                    o.TotalAmount,
                    (OrderStatusDto)(int)o.Status,
                    o.PlatformStatus,
                    o.CreatedAtPlatformUtc,
                    o.ReceivedAtUtc,
                    o.OrderedAtUtc))
                .ToList()));
    }

    /// <summary>
    /// The order report for a period and optional platform (WAS-182). Query: <c>preset</c> (today, yesterday,
    /// last7days, last30days, custom), <c>startDate</c> and <c>endDate</c> (yyyy-MM-dd, custom only), <c>platform</c>.
    /// An invalid filter answers 400 with an error code per parameter; nothing is widened.
    /// </summary>
    /// <remarks>
    /// The filter values are bound as submitted (<see cref="FromQueryAsSubmittedAttribute"/>): MVC's own binding turns a
    /// whitespace-only value into null, which would make <c>platform=%20</c> "every platform" or <c>startDate=%20</c> a
    /// missing date instead of a refused one.
    /// </remarks>
    [HttpGet("report")]
    public async Task<ActionResult<OrderReportDto>> Report(
        [FromQueryAsSubmitted] string? preset,
        [FromQueryAsSubmitted] string? startDate,
        [FromQueryAsSubmitted] string? endDate,
        [FromQueryAsSubmitted] string? platform,
        CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound("Tenant not found");

        var errors = new ModelStateDictionary();
        foreach (var parameter in new[]
                 {
                     OrderReportFilters.PresetParameter, OrderReportFilters.StartDateParameter,
                     OrderReportFilters.EndDateParameter, OrderReportFilters.PlatformParameter
                 })
        {
            if (Request.Query.TryGetValue(parameter, out var values) && values.Count > 1)
                errors.AddModelError(parameter, nameof(OrderReportFilterError.DuplicateParameter));
        }

        var resolved = OrderReportFilters.Resolve(
            preset, startDate, endDate, platform, _time.GetUtcNow().UtcDateTime, TimeZoneHelper.ResolveTurkeyTimeZone());
        foreach (var error in resolved.Errors)
            errors.AddModelError(error.Parameter, error.Error.ToString());

        if (!errors.IsValid || resolved.Filter is null)
            return ValidationProblem(errors);

        var report = await _reports.GetAsync(tenant.Id, resolved.Filter, ct);
        return Ok(ToDto(report));
    }

    private static OrderReportDto ToDto(OrderReport report)
    {
        var period = report.Filter.Period;
        return new OrderReportDto(
            new OrderReportPeriodDto(
                OrderReportFilters.PresetValue(period.Preset),
                period.StartDate,
                period.EndDate,
                period.UtcStartInclusive,
                period.UtcEndExclusive,
                period.TimeZoneId,
                period.DayCount,
                period.IsComplete),
            report.Filter.Platform is { } platform ? (FoodPlatformDto)(int)platform : null,
            report.CurrencyCode,
            report.GeneratedAtUtc,
            ToDto(report.Summary),
            report.Platforms.Select(p => new OrderReportPlatformDto((FoodPlatformDto)(int)p.Platform, ToDto(p.Figures))).ToList(),
            report.Days.Select(d => new OrderReportDayDto(d.Date, d.UtcStartInclusive, d.UtcEndExclusive, ToDto(d.Figures))).ToList(),
            report.CancellationReasons.Select(r => new OrderReportCancellationReasonDto((FoodPlatformDto)(int)r.Platform, r.ReasonCode, r.OrderCount)).ToList());
    }

    private static OrderReportFiguresDto ToDto(OrderReportFigures f) => new(
        f.OrderCount,
        f.OrderValue,
        f.CompletedOrderCount,
        f.CompletedOrderValue,
        f.AverageCompletedOrderValue,
        f.CancelledOrderCount,
        f.FailedOrderCount,
        f.InProgressOrderCount,
        f.OtherStatusOrderCount,
        f.KnownRejectedOrderCount,
        f.KnownCancelledOrderCount,
        f.UnknownOutcomeOrderCount,
        f.RestaurantOriginUnknownOutcomeOrderCount,
        f.CancellationReasonMissingOrderCount);
}
