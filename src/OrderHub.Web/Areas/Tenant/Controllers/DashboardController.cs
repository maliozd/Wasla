using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Application.Abstractions.Dashboard;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Web.Controllers;
using OrderHub.Web.Models.Dashboard;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("dashboard")]
public sealed class DashboardController : BaseController
{
    private readonly ICurrentTenantService _currentCustomer;
    private readonly IDashboardService _dashboard;

    public DashboardController(ICurrentTenantService currentCustomer, IDashboardService dashboard)
    {
        _currentCustomer = currentCustomer;
        _dashboard = dashboard;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var customer = _currentCustomer.CurrentTenant;
        if (customer is null) return NotFound();

        var dto = await _dashboard.GetTodayAsync(customer.Id, ct);
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

