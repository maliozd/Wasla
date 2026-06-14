using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Controllers;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
[Route("settings/account")]
public sealed class AccountSettingsController : BaseController
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
