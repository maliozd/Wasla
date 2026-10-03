using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Orders;
using Wasla.Web.Controllers;
using Wasla.Web.DevelopmentTools;
using Wasla.Web.Models.Help;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// Help and Guides: permanent, readable documentation for every tenant role. It is not a tour:
/// no coachmarks, replay or training controls. Action links appear only where the user's
/// existing policies allow the target page. In local Development only, an Owner also sees a separate
/// "Development tools" section (the temporary test-tenant reset); it stays reachable after guided setup
/// was skipped or completed.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]
[Route("help")]
public sealed class HelpController : BaseController
{
    private readonly ITenantNavigationAuthorizationService _navigation;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentTenantService _currentTenant;
    private readonly IWebHostEnvironment _environment;
    private readonly DevelopmentToolsOptions _developmentTools;

    public HelpController(
        ITenantNavigationAuthorizationService navigation,
        IAuthorizationService authorization,
        ICurrentTenantService currentTenant,
        IWebHostEnvironment environment,
        IOptions<DevelopmentToolsOptions> developmentTools)
    {
        _navigation = navigation;
        _authorization = authorization;
        _currentTenant = currentTenant;
        _environment = environment;
        _developmentTools = developmentTools.Value;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var permissions = await _navigation.GetPermissionsAsync(User);
        return View("Index", new HelpPageViewModel
        {
            CanOpenPlatformConnections = permissions.CanManageTenantSettings,
            CanOpenPrintBridgeSetup = permissions.CanManageDeviceSecurity,
            CanOpenPrintBridgeDevices = permissions.CanManagePrintBridgeDevices,
            CanOpenReceiptPrinterSettings = permissions.CanManageTenantSettings,
            CanOpenLiveScreen = permissions.CanViewLiveScreen,
            CanOpenOrders = permissions.CanViewOrders,
            DeliveredWindowMinutes = (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes,
            DevelopmentTenantResetSlug = await DevelopmentTenantResetSlugAsync()
        });
    }

    /// <summary>The slug to type for the reset, or null when the tool must not be shown to this user.</summary>
    private async Task<string?> DevelopmentTenantResetSlugAsync()
    {
        if (!DevelopmentToolsAvailability.IsTenantResetAvailable(_environment, _developmentTools))
            return null;

        var tenant = _currentTenant.CurrentTenant;
        if (tenant is null)
            return null;

        var owner = await _authorization.AuthorizeAsync(User, TenantPolicies.TenantOwner);
        return owner.Succeeded ? tenant.Slug : null;
    }
}
