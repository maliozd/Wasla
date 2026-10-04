using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Admin;
using Wasla.Web.Areas.Admin.Models.TenantOperations;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

/// <summary>
/// Tenant Operations Center: the paged tenant list (CentralDb only) and one tenant's operational detail, the only
/// place a tenant database is read. Both are read-only; the activate/deactivate posts predate these pages.
/// </summary>
[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/customers")]
public sealed class CustomersController : Controller
{
    private readonly ICentralAdminTenantService _customers;
    private readonly ICentralAdminTenantOperationsService _operations;
    private readonly ITenantOperationalHealthReader _health;
    private readonly IWaslaPlanCatalog _plans;
    private readonly TimeProvider _time;
    private readonly IWebHostEnvironment _environment;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(
        ICentralAdminTenantService customers,
        ICentralAdminTenantOperationsService operations,
        ITenantOperationalHealthReader health,
        IWaslaPlanCatalog plans,
        TimeProvider time,
        IWebHostEnvironment environment,
        IStringLocalizer<SharedResource> localizer,
        ILogger<CustomersController> logger)
    {
        _customers = customers;
        _operations = operations;
        _health = health;
        _plans = plans;
        _time = time;
        _environment = environment;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "q")] string? search,
        [FromQuery] string? status,
        [FromQuery] string? migration,
        [FromQuery] string? plan,
        [FromQuery] string? sort,
        [FromQuery] string? dir,
        [FromQuery] int? page,
        [FromQuery(Name = "size")] int? pageSize,
        CancellationToken ct)
    {
        var planCodes = _plans.GetPublicPlans().Select(p => p.PlanCode).ToList();
        var query = TenantListQuery.Create(search, status, migration, plan, sort, dir, page, pageSize, planCodes);
        var now = _time.GetUtcNow().UtcDateTime;

        try
        {
            var result = await _operations.GetTenantListAsync(query, ct).ConfigureAwait(false);
            return View(new AdminTenantListViewModel { Page = result, Query = result.Query, PlanCodes = planCodes, NowUtc = now });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Admin tenant list could not read CentralDb ({ExceptionType}).", ex.GetType().Name);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(new AdminTenantListViewModel { Query = query, PlanCodes = planCodes, NowUtc = now });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        TenantOperationsDetail? detail;
        try
        {
            detail = await _operations.GetTenantDetailAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Admin tenant detail could not read CentralDb ({ExceptionType}).", ex.GetType().Name);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(new AdminTenantDetailViewModel { NowUtc = _time.GetUtcNow().UtcDateTime });
        }

        if (detail is null) return NotFound(_localizer["Admin.CustomerNotFound"].Value);

        // The tenant is identified by the CentralDb record just read, never by anything else from the request.
        var health = detail.DatabaseConfigured
            ? await _health.ReadAsync(detail.Id, ct).ConfigureAwait(false)
            : TenantOperationalHealth.Unavailable(TenantDatabaseState.NotConfigured, TenantProviderMode.Unknown, detail.ReadAtUtc);

        return View(new AdminTenantDetailViewModel
        {
            Detail = detail,
            Health = health,
            Guidance = TenantOperationsGuidanceBuilder.Build(detail, health),
            TenantAddressUrl = string.IsNullOrWhiteSpace(detail.PrimaryDomain)
                ? null
                : TenantWelcomeUrlBuilder.BuildTenantAddressUrl(Request, _environment, detail.PrimaryDomain),
            NowUtc = _time.GetUtcNow().UtcDateTime
        });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var ok = await _customers.SetCustomerActiveStateAsync(id, isActive: true, ct).ConfigureAwait(false);
        if (!ok) return NotFound(_localizer["Admin.CustomerNotFound"].Value);
        return RedirectToAction(nameof(Details), new { id });
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var ok = await _customers.SetCustomerActiveStateAsync(id, isActive: false, ct).ConfigureAwait(false);
        if (!ok) return NotFound(_localizer["Admin.CustomerNotFound"].Value);
        return RedirectToAction(nameof(Details), new { id });
    }
}
