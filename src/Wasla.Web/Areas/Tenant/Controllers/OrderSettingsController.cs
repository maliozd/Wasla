using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Controllers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant, Policy = TenantPolicies.TenantManagerOrOwner)]
[Route("settings/orders")]
public sealed class OrderSettingsController : BaseController
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("/settings/order")]
    [HttpGet("/settings/order-automation")]
    public IActionResult LegacyRedirect() => RedirectPermanent("/settings/orders");
}
