using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Reports;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// The Orders report page (WAS-183). Every figure comes from the shared report (<see cref="IOrderReportService"/>,
/// docs/orders/reporting.md); this controller resolves the filter, reads the report and picks the state to show.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewReports)]
[Route("reports")]
public sealed class ReportsController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IOrderReportService _reports;
    private readonly TimeProvider _time;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(
        ICurrentTenantService currentTenant,
        IOrderReportService reports,
        TimeProvider time,
        ILogger<ReportsController> logger)
    {
        _currentTenant = currentTenant;
        _reports = reports;
        _time = time;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var vm = await BuildAsync(tenant.Id, ct);
        if (vm.State == ReportResultState.InvalidFilter)
            Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("Index", vm);
    }

    /// <summary>The result region alone, for a filter change without a page load. A refused filter answers 400 with its state.</summary>
    [HttpGet("content")]
    public async Task<IActionResult> Content(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var vm = await BuildAsync(tenant.Id, ct);
        if (vm.State == ReportResultState.Failed)
            return StatusCode(StatusCodes.Status500InternalServerError);
        if (vm.State == ReportResultState.InvalidFilter)
            Response.StatusCode = StatusCodes.Status400BadRequest;
        return PartialView("_ReportResult", vm);
    }

    private async Task<ReportPageViewModel> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        var query = ReportPageQuery.From(Request.Query);
        var timeZone = TimeZoneHelper.ResolveTurkeyTimeZone();
        var now = _time.GetUtcNow().UtcDateTime;
        var today = OrderReportFilters.LocalToday(now, timeZone);

        var resolved = OrderReportFilters.Resolve(query.Preset, query.StartDate, query.EndDate, query.Platform, now, timeZone);
        var problems = query.DuplicatedParameters
            .Select(p => new ReportFilterProblem(p, ProblemKey(OrderReportFilterError.DuplicateParameter)))
            .Concat(resolved.Errors.Select(e => new ReportFilterProblem(e.Parameter, ProblemKey(e.Error))))
            .ToList();
        if (problems.Count > 0 || resolved.Filter is null)
            return new ReportPageViewModel { Query = query, State = ReportResultState.InvalidFilter, Today = today, Problems = problems };

        var filter = resolved.Filter;
        try
        {
            var report = await _reports.GetAsync(tenantId, filter, ct);

            var comparison = ReportComparisonState.Off;
            OrderReport? previous = null;
            string? completeDaysUrl = null;
            if (query.Compare && query.Section == ReportSections.Overview)
            {
                if (!filter.Period.IsComplete)
                {
                    comparison = ReportComparisonState.NeedsCompletePeriod;
                    var end = today.AddDays(-1);
                    completeDaysUrl = query.UrlForCustomRange(end.AddDays(1 - filter.Period.DayCount), end, compare: true);
                }
                else if (OrderReportFilters.PrecedingPeriod(filter, now) is { } preceding)
                {
                    previous = await _reports.GetAsync(tenantId, preceding, ct);
                    comparison = ReportComparisonState.Active;
                }
                else
                {
                    comparison = ReportComparisonState.Unavailable;
                }
            }

            var platformReports = new List<OrderReport>();
            if (query.Section == ReportSections.Platforms && filter.Platform is null)
            {
                foreach (var row in report.Platforms)
                    platformReports.Add(await _reports.GetAsync(tenantId, filter with { Platform = row.Platform }, ct));
            }

            var hasOrders = report.Summary.OrderCount > 0 || previous?.Summary.OrderCount > 0;
            return new ReportPageViewModel
            {
                Query = query,
                State = hasOrders ? ReportResultState.Report : ReportResultState.NoOrders,
                Today = today,
                Report = report,
                Comparison = comparison,
                Previous = previous,
                PlatformReports = platformReports,
                CompleteDaysUrl = completeDaysUrl,
                GeneratedAtLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(report.GeneratedAtUtc, DateTimeKind.Utc), timeZone)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Order report failed: {ExceptionType}", ex.GetType().Name);
            return new ReportPageViewModel { Query = query, State = ReportResultState.Failed, Today = today };
        }
    }

    private static string ProblemKey(OrderReportFilterError error) => "Reports.FilterError." + error;
}
