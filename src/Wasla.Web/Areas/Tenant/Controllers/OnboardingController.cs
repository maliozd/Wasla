using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wasla.Web.Routing;
using Wasla.Web.Security;

namespace Wasla.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Route("onboarding")]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
public sealed class OnboardingController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => Redirect("/dashboard");
}
