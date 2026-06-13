using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Web.Controllers;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("settings/order")]
public sealed class OrderSettingsController : BaseController
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("/settings/order-automation")]
    public IActionResult LegacyRedirect() => RedirectPermanent("/settings/order");
}
