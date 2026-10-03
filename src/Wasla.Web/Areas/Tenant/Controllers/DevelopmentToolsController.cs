using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.DevelopmentTools;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Web.Controllers;
using Wasla.Web.DevelopmentTools;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// TEMPORARY Development tool: resets the current test tenant so onboarding can be tested again from the first
/// Start/Skip choice. The controller exists only when the tool is available (Development environment and
/// DevelopmentTools:EnableTenantReset; see <see cref="DevelopmentToolsConvention"/>), so elsewhere every request
/// gets 404. Owner only. The tenant comes from the authenticated request; nothing identifying a tenant or user
/// is read from the form except the typed confirmation, which must equal the current tenant's slug.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.TenantOwner)]
[DevelopmentTenantResetTool]
[Route("development-tools")]
public sealed class DevelopmentToolsController : BaseController
{
    /// <summary>The success message explains the manual browser step, so it stays up longer.</summary>
    public const int ResetToastDurationMs = 9000;

    /// <summary>Wasla-owned browser keys about onboarding for this tenant, cleared after a successful reset.</summary>
    public static IReadOnlyList<string> OnboardingBrowserKeys(Guid tenantId) =>
    [
        $"Wasla.setupWelcome.dismissed.{tenantId}"
    ];

    private readonly ICurrentTenantService _currentTenant;
    private readonly ITenantDevelopmentResetService _reset;
    private readonly IWebHostEnvironment _environment;
    private readonly DevelopmentToolsOptions _options;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public DevelopmentToolsController(
        ICurrentTenantService currentTenant,
        ITenantDevelopmentResetService reset,
        IWebHostEnvironment environment,
        IOptions<DevelopmentToolsOptions> options,
        IStringLocalizer<SharedResource> localizer)
    {
        _currentTenant = currentTenant;
        _reset = reset;
        _environment = environment;
        _options = options.Value;
        _localizer = localizer;
    }

    [HttpPost("tenant-reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTenant([FromForm] string? confirmSlug, CancellationToken ct)
    {
        // The route should not exist outside Development; check again so a misconfiguration can never expose it.
        if (!DevelopmentToolsAvailability.IsTenantResetAvailable(_environment, _options))
            return NotFound();

        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return NotFound();

        if (!string.Equals(confirmSlug, tenant.Slug, StringComparison.Ordinal))
        {
            TempData["Error"] = _localizer["DevelopmentTools.Reset.SlugMismatch"].Value;
            return LocalRedirect(HelpUrl);
        }

        var result = await _reset.ResetAsync(tenant.Id, ct);
        if (!result.Succeeded)
        {
            TempData["Error"] = _localizer[result.PartiallyApplied ? "DevelopmentTools.Reset.Partial" : "DevelopmentTools.Reset.Failed"].Value;
            return LocalRedirect(HelpUrl);
        }

        TempData["Success"] = _localizer["DevelopmentTools.Reset.Succeeded"].Value;
        TempData["SuccessDurationMs"] = ResetToastDurationMs;
        TempData["ClearBrowserKeys"] = JsonSerializer.Serialize(OnboardingBrowserKeys(tenant.Id));
        return LocalRedirect("/dashboard");
    }

    private const string HelpUrl = "/help#help-development-tools";
}
