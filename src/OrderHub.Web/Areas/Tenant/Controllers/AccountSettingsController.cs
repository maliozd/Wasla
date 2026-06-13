using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Web.Controllers;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Customer)]
[Route("settings/account")]
public sealed class AccountSettingsController : BaseController
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
