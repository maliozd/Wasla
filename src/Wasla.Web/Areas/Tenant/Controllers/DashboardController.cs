using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Dashboard;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using Wasla.Web.Ui;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewReports)]
[Route("dashboard")]
public sealed class DashboardController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IDashboardService _dashboard;
    private readonly ITenantSetupStatusService _setup;
    private readonly IAuthorizationService _authorization;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        ICurrentTenantService currentTenant,
        IDashboardService dashboard,
        ITenantSetupStatusService setup,
        IAuthorizationService authorization,
        IStringLocalizer<SharedResource> localizer,
        ILogger<DashboardController> logger)
    {
        _currentTenant = currentTenant;
        _dashboard = dashboard;
        _setup = setup;
        _authorization = authorization;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var dto = await _dashboard.GetTodayAsync(tenant.Id, ct);
        var vm = new DashboardViewModel
        {
            TodayOrderCount = dto.TodayOrderCount,
            TodayRevenue = dto.TodayRevenue,
            ActiveOrderCount = dto.ActiveOrderCount,
            CancelledOrderCount = dto.CancelledOrderCount,
            PlatformSummary = dto.PlatformSummary
                .Select(p => new DashboardViewModel.PlatformSummaryRow { Platform = p.Platform, Count = p.Count, Revenue = p.Revenue })
                .ToList(),
            RecentOrders = dto.RecentOrders
                .Select(o => new DashboardViewModel.RecentOrderRow
                {
                    Id = o.Id,
                    Platform = o.Platform,
                    ExternalOrderCode = o.ExternalOrderCode,
                    Status = o.Status,
                    CustomerName = o.CustomerName,
                    TotalAmount = o.TotalAmount,
                    ReceivedAtUtc = o.ReceivedAtUtc
                })
                .ToList()
        };

        vm.Setup = await TryLoadSetupAsync(tenant.Id, tenant.Name, ct);
        return View("Index", vm);
    }

    [ValidateAntiForgeryToken]
    [Authorize(Policy = TenantPolicies.CanManageTenantSettings)]
    [HttpPost("complete-setup")]
    public async Task<IActionResult> CompleteSetup(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var completed = await _setup.CompleteGuidanceAsync(tenant.Id, ct);
        TempData[completed ? "Success" : "Error"] = completed
            ? _localizer["Setup.GuidanceCompleted"].Value
            : _localizer["Setup.GuidanceCompleteFailed"].Value;

        return Redirect("/dashboard");
    }

    private async Task<TenantSetupPanelViewModel?> TryLoadSetupAsync(
        Guid tenantId,
        string restaurantName,
        CancellationToken ct)
    {
        var allowed = await _authorization.AuthorizeAsync(User, resource: null, TenantPolicies.CanManageTenantSettings);
        if (!allowed.Succeeded)
            return null;

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId");
        if (!Guid.TryParse(userIdValue, out var userId))
            return null;

        try
        {
            var status = await _setup.GetAsync(tenantId, userId, ct);
            if (status.IsSetupGuidanceCompleted)
                return null;

            return TenantSetupPanelMapper.Map(status, restaurantName, tenantId, _localizer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Tenant setup panel failed: {ExceptionType}",
                ex.GetType().Name);
            return null;
        }
    }
}

