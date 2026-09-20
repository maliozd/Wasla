using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Controllers;
using Wasla.Web.Models.Settings;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.CanManageOrderNotifications)]
[Route("settings/orders")]
public sealed class OrderSettingsController : BaseController
{
    private readonly IAuthorizationService _authorization;

    public OrderSettingsController(IAuthorizationService authorization)
    {
        _authorization = authorization;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        _ = ct;
        var canManageAutomation = (await _authorization
            .AuthorizeAsync(User, resource: null, policyName: TenantPolicies.CanManageOrderAutomation)
            .ConfigureAwait(false)).Succeeded;
        var canManageNotifications = (await _authorization
            .AuthorizeAsync(User, resource: null, policyName: TenantPolicies.CanManageOrderNotifications)
            .ConfigureAwait(false)).Succeeded;

        return View(new OrderSettingsPageViewModel
        {
            CanManageOrderAutomation = canManageAutomation,
            CanManageOrderNotifications = canManageNotifications
        });
    }

    [HttpGet("/settings/order")]
    [HttpGet("/settings/order-automation")]
    public IActionResult LegacyRedirect() => RedirectPermanent("/settings/orders");
}
