using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.Models.PlatformConnections;
using Wasla.Web.Routing;
using Wasla.Web.Security;
using Microsoft.Extensions.Localization;
using Wasla.Domain.Enums;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageTenantSettings)]
[Route("platform-connections")]
public sealed class PlatformConnectionsController : BaseController
{
    private readonly ICurrentTenantService _currentTenant;
    private readonly IPlatformConnectionService _connections;
    private readonly IValidator<CreatePlatformConnectionCommand> _createValidator;
    private readonly IStringLocalizer<Wasla.Web.SharedResource> _localizer;

    public PlatformConnectionsController(
        ICurrentTenantService currentTenant,
        IPlatformConnectionService connections,
        IValidator<CreatePlatformConnectionCommand> createValidator,
        IStringLocalizer<Wasla.Web.SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _connections = connections;
        _createValidator = createValidator;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var list = await _connections.GetListAsync(tenant.Id, ct);
        var rows = list.Select(x => new PlatformConnectionListViewModel.Row
        {
            Id = x.Id,
            Platform = x.Platform,
            StoreId = x.StoreId,
            IsActive = x.IsActive,
            ConsecutiveFailures = x.ConsecutiveFailures,
            CircuitOpenUntilUtc = x.CircuitOpenUntilUtc,
            LastSuccessfulSyncUtc = x.LastSuccessfulSyncUtc
        }).ToList();

        return View("Index", new PlatformConnectionListViewModel { Connections = rows });
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var c = await _connections.GetByIdAsync(tenant.Id, id, ct);
        if (c is null) return NotFound();

        var vm = new EditPlatformConnectionViewModel
        {
            Id = c.Id,
            Platform = c.Platform,
            StoreId = c.StoreId,
            SupplierId = c.SupplierId,
            ExecutorEmail = c.ExecutorEmail,
            IsActive = c.IsActive,
            ApiKey = string.Empty,
            ApiSecret = string.Empty
        };

        return View("Edit", vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, EditPlatformConnectionViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (!ModelState.IsValid) return View("Edit", model);

        var cmd = new UpdatePlatformConnectionCommand(
            Platform: model.Platform,
            StoreId: model.StoreId,
            IsActive: model.IsActive,
            SyncIntervalSeconds: null,
            SupplierId: model.SupplierId,
            ExecutorEmail: model.ExecutorEmail,
            ApiKey: string.IsNullOrWhiteSpace(model.ApiKey) ? null : model.ApiKey,
            ApiSecret: string.IsNullOrWhiteSpace(model.ApiSecret) ? null : model.ApiSecret);

        var result = await _connections.UpdateAsync(tenant.Id, id, cmd, ct);
        if (!result.Succeeded)
        {
            if (string.Equals(result.ErrorCode, "Duplicate", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(EditPlatformConnectionViewModel.Platform), _localizer["PlatformConnections.DuplicateError"].Value);
            }
            else
            {
                ModelState.AddModelError(string.Empty, _localizer["PlatformConnections.UpdateFailed"].Value);
            }
            return View("Edit", model);
        }

        TempData["Success"] = _localizer["PlatformConnections.Updated"].Value;
        return RedirectToAction("Index");
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/toggle-active")]
    public async Task<IActionResult> ToggleActive(Guid id, [FromForm] bool isActive, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var ok = await _connections.SetActiveAsync(tenant.Id, id, isActive, ct);
        if (!ok) return NotFound(new { succeeded = false });

        return Ok(new { succeeded = true });
    }

    [HttpGet("create")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var model = new CreatePlatformConnectionViewModel();
        await PopulateConfiguredPlatformsAsync(model, ct);
        return View(model);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("create")]
    public async Task<IActionResult> Create(CreatePlatformConnectionViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (!ModelState.IsValid)
        {
            await PopulateConfiguredPlatformsAsync(model, ct);
            return View(model);
        }

        var cmd = new CreatePlatformConnectionCommand(
            model.Platform,
            model.StoreId,
            model.ApiKey,
            model.ApiSecret,
            model.IsActive,
            model.SupplierId,
            model.ExecutorEmail);

        var validation = await _createValidator.ValidateAsync(cmd, ct);
        if (!validation.IsValid)
        {
            foreach (var e in validation.Errors)
                ModelState.AddModelError(e.PropertyName, _localizer[e.ErrorMessage].Value);
            await PopulateConfiguredPlatformsAsync(model, ct);
            return View(model);
        }

        var result = await _connections.CreateAsync(tenant.Id, cmd, ct);
        if (!result.Succeeded)
        {
            if (string.Equals(result.ErrorCode, "Duplicate", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(CreatePlatformConnectionViewModel.Platform), _localizer["PlatformConnections.DuplicateError"].Value);
            }
            else
            {
                ModelState.AddModelError(string.Empty, result.ErrorMessage ?? _localizer["PlatformConnections.CreateFailed"].Value);
            }
            await PopulateConfiguredPlatformsAsync(model, ct);
            return View(model);
        }

        TempData["Success"] = _localizer["Common.Saved"].Value;
        return RedirectToAction("Index");
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        return await SetActive(id, true, ct);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        return await SetActive(id, false, ct);
    }

    private async Task PopulateConfiguredPlatformsAsync(CreatePlatformConnectionViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
        {
            model.ConfiguredPlatforms = [];
            return;
        }

        var list = await _connections.GetListAsync(tenant.Id, ct);
        model.ConfiguredPlatforms = list.Select(x => x.Platform).Distinct().ToArray();
    }

    private async Task<IActionResult> SetActive(Guid id, bool isActive, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        var ok = await _connections.SetActiveAsync(tenant.Id, id, isActive, ct);
        if (!ok) return NotFound();

        TempData["Success"] = isActive
            ? _localizer["PlatformConnections.Activated"].Value
            : _localizer["PlatformConnections.Deactivated"].Value;
        return RedirectToAction("Index");
    }
}

