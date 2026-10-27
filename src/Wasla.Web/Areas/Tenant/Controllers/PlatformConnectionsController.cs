using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.GuidedSetup;
using Wasla.Web.Controllers;
using Wasla.Web.GuidedSetup;
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
    private readonly IGuidedSetupCoordinator _guidedSetup;

    public PlatformConnectionsController(
        ICurrentTenantService currentTenant,
        IPlatformConnectionService connections,
        IValidator<CreatePlatformConnectionCommand> createValidator,
        IStringLocalizer<Wasla.Web.SharedResource> localizer,
        IGuidedSetupCoordinator guidedSetup)
    {
        _currentTenant = currentTenant;
        _connections = connections;
        _createValidator = createValidator;
        _localizer = localizer;
        _guidedSetup = guidedSetup;
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

        var guidedSetup = await _guidedSetup.GetSectionPanelAsync(
            tenant.Id, CurrentUserId, User, GuidedSetupSections.PlatformConnections, ct);

        return View("Index", new PlatformConnectionListViewModel
        {
            Connections = rows,
            GuidedSetup = guidedSetup
        });
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
            ApiSecret = string.Empty,
            HasStoredApiKey = c.HasApiKey,
            HasStoredApiSecret = c.HasApiSecret
        };

        return View("Edit", vm);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, EditPlatformConnectionViewModel model, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (!ModelState.IsValid) return await EditAgainAsync(tenant.Id, id, model, ct);

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
            else if (string.Equals(result.ErrorCode, "Validation", StringComparison.Ordinal) && result.ErrorMessage is not null)
            {
                // A resource key from the service's validation (store id, credentials); it names the problem, never the value.
                ModelState.AddModelError(string.Empty, _localizer[result.ErrorMessage].Value);
            }
            else
            {
                ModelState.AddModelError(string.Empty, _localizer["PlatformConnections.UpdateFailed"].Value);
            }
            return await EditAgainAsync(tenant.Id, id, model, ct);
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
            return await CreateAgainAsync(model, ct);

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
            return await CreateAgainAsync(model, ct);
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
            return await CreateAgainAsync(model, ct);
        }

        TempData["Success"] = _localizer["Common.Saved"].Value;
        return RedirectToAction("Index");
    }

    /// <summary>
    /// The confirmation dialog's Remove. A connection this tenant no longer has (removed in another tab, a repeated
    /// submission, or an id that was never its own) changes nothing and says so, without telling the cases apart.
    /// </summary>
    [ValidateAntiForgeryToken]
    [HttpPost("{id:guid}/remove")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null) return NotFound();

        if (await _connections.RemoveAsync(tenant.Id, id, ct))
            TempData["Success"] = _localizer["PlatformConnections.Removed"].Value;
        else
            TempData["Error"] = _localizer["PlatformConnections.AlreadyRemoved"].Value;

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

    /// <summary>
    /// Shows the create form again after a failed submission: the typed credentials are dropped from the model and from
    /// ModelState (whose attempted values the input helpers would otherwise prefer), and the form asks for them again.
    /// The other fields and every validation error are kept.
    /// </summary>
    private async Task<IActionResult> CreateAgainAsync(CreatePlatformConnectionViewModel model, CancellationToken ct)
    {
        model.CredentialsNeedReentry = !string.IsNullOrEmpty(model.ApiKey) || !string.IsNullOrEmpty(model.ApiSecret);
        model.ApiKey = string.Empty;
        model.ApiSecret = string.Empty;
        ForgetSubmittedCredentials();
        await PopulateConfiguredPlatformsAsync(model, ct);
        return View("Create", model);
    }

    /// <summary>
    /// Shows the edit form again after a failed submission, as <see cref="CreateAgainAsync"/> does. Which credentials are
    /// stored is read again from the tenant's own connection; a connection this tenant does not have is not found.
    /// </summary>
    private async Task<IActionResult> EditAgainAsync(Guid tenantId, Guid id, EditPlatformConnectionViewModel model, CancellationToken ct)
    {
        model.CredentialsNeedReentry = !string.IsNullOrEmpty(model.ApiKey) || !string.IsNullOrEmpty(model.ApiSecret);
        model.ApiKey = null;
        model.ApiSecret = null;
        ForgetSubmittedCredentials();

        var stored = await _connections.GetByIdAsync(tenantId, id, ct);
        if (stored is null) return NotFound();

        model.Id = stored.Id;
        model.HasStoredApiKey = stored.HasApiKey;
        model.HasStoredApiSecret = stored.HasApiSecret;
        return View("Edit", model);
    }

    private void ForgetSubmittedCredentials()
    {
        foreach (var key in CredentialFields)
        {
            if (ModelState.TryGetValue(key, out var entry))
            {
                entry.RawValue = null;
                entry.AttemptedValue = null;
            }
        }
    }

    private static readonly string[] CredentialFields =
    [
        nameof(CreatePlatformConnectionViewModel.ApiKey),
        nameof(CreatePlatformConnectionViewModel.ApiSecret)
    ];

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

