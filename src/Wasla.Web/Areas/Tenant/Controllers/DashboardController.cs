using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Dashboard;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Dashboard;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("dashboard")]
public sealed class DashboardController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IDashboardService _dashboard;

    public DashboardController(ICurrentTenantService currentTenant, IDashboardService dashboard)
    {
        _currentTenant = currentTenant;
        _dashboard = dashboard;
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

        return View("Index", vm);
    }
}

