using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderHub.Web.Routing;
using OrderHub.Web.Security;

namespace OrderHub.Web.Areas.Tenant.Controllers;

[Area(AreaNames.Tenant)]
[Route("onboarding")]
[Authorize(AuthenticationSchemes = AuthSchemes.Tenant)]
public sealed class OnboardingController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();
}
