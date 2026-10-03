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
/// <para>
/// Guided setup and order training are Owner-only: every action here, read or write, requires the TenantOwner policy
/// (the same policy that shows the card and panels), so another role is refused before any state is read or written.
/// </para>
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.TenantOwner)]
[Route("guided-setup")]
public sealed class GuidedSetupController : BaseController
{
    /// <summary>
    /// The "guided setup closed" and "training completed" toasts explain a permanent change, so they
    /// stay longer than the default success toast.
    /// </summary>
    public const int ClosedToastDurationMs = 4500;

    /// <summary>The form value the Live Screen sends so Skip and End return there.</summary>
    public const string LiveScreenOrigin = "live-screen";
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

    /// <param name="origin">Only <c>live-screen</c> is recognised: it returns there instead of the home page.</param>
    [HttpPost("skip")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Skip([FromForm] string? origin, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.SkipAsync(tenantId, userId, User, ct), origin);

    /// <summary>Ending a started journey discards progress, so the confirmation dialog posts <c>confirmed=true</c>.</summary>
    /// <param name="origin">Only <c>live-screen</c> is recognised: it returns there instead of the home page.</param>
    [HttpPost("end")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> End([FromForm] bool confirmed, [FromForm] string? origin, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.EndAsync(tenantId, userId, User, confirmed, ct), origin);

    [HttpPost("continue")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Continue(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.ContinueAsync(tenantId, userId, User, ct));

    /// <summary>"I'll set it up later": defers the section and moves to the next permitted one.</summary>
    [HttpPost("advance")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Advance([FromForm] string? section, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.AdvanceAsync(tenantId, userId, User, section, ct));

    /// <summary>Continues after the section was set up successfully; the server re-checks that it is ready.</summary>
    [HttpPost("section-continue")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ContinueFromSection([FromForm] string? section, CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.ContinueFromSectionAsync(tenantId, userId, User, section, ct));

    /// <summary>
    /// Read-only readiness for the section panel, so a page that finishes a setup (e.g. pairing Print Bridge)
    /// can show the connected state without a reload. It never changes guided-setup state.
    /// </summary>
    [HttpGet("section-status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public async Task<IActionResult> SectionStatus([FromQuery] string? section, CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId");
        if (!Guid.TryParse(userIdValue, out var userId))
            return Forbid(AuthSchemes.Tenant);

        var status = await _coordinator.GetSectionStatusAsync(tenant.Id, userId, User, section, ct);
        return Ok(new { current = status.IsCurrent, ready = status.IsReady });
    }

    /// <summary>
    /// Read-only: the connected Print Bridge panel's "get to know your device" link. Opens the device guide (the one
    /// connected device's page, or the device list), or wherever the user's guided setup actually is now. The target
    /// is resolved on the server from the authenticated tenant; nothing is written and no id comes from the request.
    /// </summary>
    [HttpGet("print-bridge/device")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None, Duration = 0)]
    public async Task<IActionResult> PrintBridgeDevice(CancellationToken ct)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("UserId");
        if (!Guid.TryParse(userIdValue, out var userId))
            return Forbid(AuthSchemes.Tenant);

        return LocalRedirect(await _coordinator.GetCurrentSectionUrlAsync(tenant.Id, userId, User, ct));
    }

    /// <summary>Starts the practice order on the Live Screen (or returns to the open one).</summary>
    [HttpPost("training/practice")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
    public Task<IActionResult> StartPractice(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.StartPracticeAsync(tenantId, userId, User, ct));

    /// <summary>Completes guided setup after the delivered practice order was explained.</summary>
    [HttpPost("training/complete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = TenantPolicies.CanManageOrders)]
    public Task<IActionResult> CompleteTraining(CancellationToken ct) =>
        RunAsync((tenantId, userId) => _coordinator.CompleteTrainingAsync(tenantId, userId, User, ct));

    /// <summary>A fixed allow-list; any other value keeps the command's own destination.</summary>
    private static string? ReturnUrlFor(string? origin) =>
        string.Equals(origin, LiveScreenOrigin, StringComparison.Ordinal) ? GuidedSetupCoordinator.LiveScreenUrl : null;

    private async Task<IActionResult> RunAsync(Func<Guid, Guid, Task<GuidedSetupCommandResult>> command, string? origin = null)
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
        if (result.SuccessMessageKey is GuidedSetupCoordinator.ClosedMessageKey or GuidedSetupCoordinator.TrainingCompletedMessageKey)
            TempData["SuccessDurationMs"] = ClosedToastDurationMs;
        if (result.ErrorMessageKey is not null)
            TempData["Error"] = _localizer[result.ErrorMessageKey].Value;

        return LocalRedirect(ReturnUrlFor(origin) ?? result.RedirectUrl);
    }
}
