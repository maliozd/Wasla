using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Orders;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Help;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

/// <summary>
/// Help and Guides: permanent, readable documentation for every tenant role. It is not a tour:
/// no coachmarks, replay or training controls. Action links appear only where the user's
/// existing policies allow the target page.
/// </summary>
[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanViewOrders)]
[Route("help")]
public sealed class HelpController : BaseController
{
    private readonly ITenantNavigationAuthorizationService _navigation;

    public HelpController(ITenantNavigationAuthorizationService navigation)
    {
        _navigation = navigation;
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
            DeliveredWindowMinutes = (int)LiveScreenVisibility.RecentDeliveredWindow.TotalMinutes
        });
    }
}
