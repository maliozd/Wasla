using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Admin;
using OrderHub.Web.Areas.Admin.Models;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Admin.Controllers;

[Area(AreaNames.Admin)]
[Authorize(AuthenticationSchemes = AuthSchemes.CentralAdmin)]
[Route("admin/customers")]
public sealed class CustomersController : Controller
{
    private readonly ICentralAdminCustomerService _customers;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CustomersController(ICentralAdminCustomerService customers, IStringLocalizer<SharedResource> localizer)
    {
        _customers = customers;
        _localizer = localizer;
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
            Customers = data.Customers.Select(c => new CentralAdminCustomerListItemViewModel
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

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var c = await _customers.GetCustomerAsync(id, ct).ConfigureAwait(false);
        if (c is null) return NotFound(_localizer["Admin.CustomerNotFound"].Value);

        var vm = new CentralAdminCustomerDetailViewModel
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
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        };
        return View(vm);
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

