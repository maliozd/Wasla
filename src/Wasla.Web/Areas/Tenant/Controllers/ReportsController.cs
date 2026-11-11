using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Reports;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Reports;
using Wasla.Application.Time;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Reports;
using Wasla.Web.Reports.Export;
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

        var (filter, problems) = ResolveFilter(query, now, timeZone);
        if (filter is null)
            return new ReportPageViewModel { Query = query, State = ReportResultState.InvalidFilter, Today = today, Problems = problems };

        query = query.Canonicalized(filter);
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

            var hasOrders = report.Summary.OrderCount > 0 || previous?.Summary.OrderCount > 0;
            return new ReportPageViewModel
            {
                Query = query,
                State = hasOrders ? ReportResultState.Report : ReportResultState.NoOrders,
                Today = today,
                Report = report,
                Comparison = comparison,
                Previous = previous,
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

    /// <summary>
    /// The report as a CSV, PDF or Excel (.xlsx) file (WAS-184, docs/orders/reporting.md#export): the summary, platform
    /// rows and day rows of the same filter, resolved by the same rules and with the same default as the page; section and
    /// comparison do not apply. The tenant comes from the session, the role policy is the page's, and one read of the
    /// shared report feeds the file. A refused filter answers 400, an export already running for this user 429, a failure
    /// 500, each with a short localized text and no file.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromServices] ReportExportGate gate,
        [FromServices] IStringLocalizer<SharedResource> localizer,
        CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        // Tenant figures: never cached or sniffed, whatever the answer.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        var formats = Request.Query[ReportExportFormats.Parameter];
        if (formats.Count != 1 || !ReportExportFormats.TryParse(formats[0], out var format))
            return Refuse(StatusCodes.Status400BadRequest, localizer["Reports.Export.UnknownFormat"].Value);

        var query = ReportPageQuery.From(Request.Query);
        var (filter, problems) = ResolveFilter(query, _time.GetUtcNow().UtcDateTime, TimeZoneHelper.ResolveTurkeyTimeZone());
        if (filter is null)
        {
            var messages = problems.Select(p => localizer[p.ResourceKey, OrderReportFilters.MaxRangeDays].Value).Distinct();
            return Refuse(StatusCodes.Status400BadRequest, string.Join(" ", messages));
        }

        var admission = gate.TryEnter(tenant.Id, CurrentUserId, out var lease);
        if (admission != ReportExportAdmission.Admitted)
        {
            Response.Headers.RetryAfter = "5";
            var key = admission == ReportExportAdmission.AlreadyRunning ? "Reports.Export.AlreadyRunning" : "Reports.Export.Busy";
            return Refuse(StatusCodes.Status429TooManyRequests, localizer[key].Value);
        }

        using (lease)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ExportTimeout);
            try
            {
                var report = await _reports.GetAsync(tenant.Id, filter, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                var document = new ReportExportBuilder(localizer).Build(report, tenant.Name);
                var bytes = format switch
                {
                    ReportExportFormat.Csv => ReportCsvWriter.Write(document),
                    ReportExportFormat.Pdf => ReportPdfWriter.Write(document),
                    _ => ReportXlsxWriter.Write(document)
                };
                return File(bytes, ReportExportFormats.ContentType(format), ReportExportFormats.FileName(document, format));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser left; there is nobody to answer.
                throw;
            }
            catch (Exception ex)
            {
                // A timeout lands here too. Only the format and the exception type are logged: no figures, no names.
                _logger.LogWarning("Order report export failed: {Format} {ExceptionType}", format, ex.GetType().Name);
                return Refuse(StatusCodes.Status500InternalServerError, localizer["Reports.Export.Failed"].Value);
            }
        }
    }

    /// <summary>
    /// The longest the export's report read may take. Writing the file cannot be interrupted; its size is bounded by the
    /// 92-day period limit.
    /// </summary>
    internal static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(60);

    private static ContentResult Refuse(int status, string message) => new()
    {
        StatusCode = status,
        Content = message,
        ContentType = "text/plain; charset=utf-8"
    };

    /// <summary>The page's and the export's filter, or every reason it was refused, a repeated parameter included.</summary>
    private static (OrderReportFilter? Filter, List<ReportFilterProblem> Problems) ResolveFilter(ReportPageQuery query, DateTime now, TimeZoneInfo timeZone)
    {
        var resolved = OrderReportFilters.Resolve(query.Preset, query.StartDate, query.EndDate, query.Platform, now, timeZone);
        var problems = query.DuplicatedParameters
            .Select(p => new ReportFilterProblem(p, ProblemKey(OrderReportFilterError.DuplicateParameter)))
            .Concat(resolved.Errors.Select(e => new ReportFilterProblem(e.Parameter, ProblemKey(e.Error))))
            .ToList();
        return problems.Count > 0 || resolved.Filter is null ? (null, problems) : (resolved.Filter, problems);
    }

    private static string ProblemKey(OrderReportFilterError error) => "Reports.FilterError." + error;
}
