using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.Options;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    private readonly CustomerOnboardingOptions _onboardingOptions;

    public HomeController(IOptions<CustomerOnboardingOptions> onboardingOptions)
    {
        _onboardingOptions = onboardingOptions.Value;
    }

    public IActionResult Index()
    {
        return View();
    }

    [AllowAnonymous]
    [HttpGet("/tenant-address-required")]
    public IActionResult TenantAddressRequired([FromQuery] string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["SignupUrl"] = BuildSignupUrl();
        return View();
    }

    [AllowAnonymous]
    [HttpGet("/customer-access-required")]
    public IActionResult CustomerAccessRequired([FromQuery] string? returnUrl = null)
    {
        var redirect = "/tenant-address-required";
        if (!string.IsNullOrWhiteSpace(returnUrl))
            redirect += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";

        return Redirect(redirect);
    }

    [AllowAnonymous]
    [HttpGet("/tenant-not-found")]
    public IActionResult TenantNotFound([FromQuery] string? host = null)
    {
        ViewData["RequestedHost"] = host;
        ViewData["SignupUrl"] = BuildSignupUrl();
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View();
    }

    private string BuildSignupUrl()
    {
        var request = HttpContext.Request;
        var portSuffix = request.Host.Port is int port ? $":{port}" : string.Empty;

        if (string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"{request.Scheme}://localhost{portSuffix}/signup";
        }

        var marketingDomain = _onboardingOptions.MarketingBaseDomain.Trim().TrimStart('.');
        return $"{request.Scheme}://{marketingDomain}{portSuffix}/signup";
    }
}
