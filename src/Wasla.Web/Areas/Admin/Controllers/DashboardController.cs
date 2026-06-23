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
    private readonly ICentralAdminPendingRegistrationService _pendingRegs;

    public DashboardController(
        ICentralAdminTenantService customers,
        ICentralAdminPendingRegistrationService pendingRegs)
    {
        _customers = customers;
        _pendingRegs = pendingRegs;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var data = await _customers.GetDashboardAsync(ct).ConfigureAwait(false);
        var attention = await _pendingRegs.GetAttentionListAsync(ct).ConfigureAwait(false);

        static CentralAdminTenantListItemViewModel MapTenant(CentralAdminTenantListItemDto c) => new()
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
        };

        var vm = new CentralAdminDashboardViewModel
        {
            TotalCustomers = data.TotalCustomers,
            ActiveCustomers = data.ActiveCustomers,
            InactiveCustomers = data.InactiveCustomers,
            Customers = data.Customers.Select(MapTenant).ToList(),
            RecentCustomers = data.RecentCustomers.Select(MapTenant).ToList(),
            RegPendingPayment = data.RegPendingPayment,
            RegPaymentReceivedSetupPending = data.RegPaymentReceivedSetupPending,
            RegProvisioned = data.RegProvisioned,
            AttentionRegistrations = attention
        };

        return View(vm);
    }
}

