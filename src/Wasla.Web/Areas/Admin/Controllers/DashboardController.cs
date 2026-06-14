using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Admin;
using Wasla.Web.Areas.Admin.Models;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Admin.Controllers;

[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin")]
public sealed class DashboardController : Controller
{
    private readonly ICentralAdminTenantService _customers;

    public DashboardController(ICentralAdminTenantService customers)
    {
        _customers = customers;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await _customers.GetDashboardAsync(ct).ConfigureAwait(false);
        var vm = new CentralAdminDashboardViewModel
        {
            TotalCustomers = data.TotalCustomers,
            ActiveCustomers = data.ActiveCustomers,
            InactiveCustomers = data.InactiveCustomers,
            Customers = data.Customers.Select(c => new CentralAdminTenantListItemViewModel
            {
                Id = c.Id,
                Name = c.Name,
                Slug = c.Slug,
                PrimaryDomain = c.PrimaryDomain,
                DatabaseName = c.DatabaseName,
                IsActive = c.IsActive,
                SchemaVersion = c.SchemaVersion,
                LastMigrationAt = c.LastMigrationAt,
                LastMigrationResult = c.LastMigrationResult,
                CreatedAt = c.CreatedAt
            }).ToList()
        };

        return View(vm);
    }
}

