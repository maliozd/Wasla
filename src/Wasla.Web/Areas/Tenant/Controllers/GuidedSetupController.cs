using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// Guided-setup commands for the signed-in user. The user always comes from the authenticated
/// principal, never from the request; which sections apply is decided by the coordinator from the
/// existing policies. Every command is a POST with antiforgery validation.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]
[Route("guided-setup")]
public sealed class GuidedSetupController : BaseController
{
    /// <summary>The "guided setup closed" toast explains a permanent choice, so it stays longer than the default success toast.</summary>
    public const int ClosedToastDurationMs = 4500;

    private readonly ICurrentTenantService _currentTenant;
    private readonly IGuidedSetupCoordinator _coordinator;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public GuidedSetupController(
        ICurrentTenantService currentTenant,
        IGuidedSetupCoordinator coordinator,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _coordinator = coordinator;
        _localizer = localizer;
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Start(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.StartAsync(tenantId, userId, User, ct));

    [HttpPost("skip")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Skip(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.SkipAsync(tenantId, userId, User, ct));

    /// <summary>Ending a started journey discards progress, so the confirmation dialog posts <c>confirmed=true</c>.</summary>
    [HttpPost("end")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> End([FromForm] bool confirmed, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.EndAsync(tenantId, userId, User, confirmed, ct));

    [HttpPost("continue")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Continue(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.ContinueAsync(tenantId, userId, User, ct));

    /// <summary>Continue from a ready section, or set it up later; both move to the next permitted section.</summary>
    [HttpPost("advance")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Advance([FromForm] string? section, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.AdvanceAsync(tenantId, userId, User, section, ct));

    private async Task<IActionResult> RunAsync(Func<Guid, Guid, Task<GuidedSetupCommandResult>> command)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId");
        if (!Guid.TryParse(userIdValue, out var userId))
            return Forbid(AuthSchemes.Tenant);

        var result = await command(tenant.Id, userId);
        if (result.SuccessMessageKey is not null)
            TempData["Success"] = _localizer[result.SuccessMessageKey].Value;
        if (result.SuccessMessageKey == GuidedSetupCoordinator.ClosedMessageKey)
            TempData["SuccessDurationMs"] = ClosedToastDurationMs;
        if (result.ErrorMessageKey is not null)
            TempData["Error"] = _localizer[result.ErrorMessageKey].Value;

        return LocalRedirect(result.RedirectUrl);
    }
}
