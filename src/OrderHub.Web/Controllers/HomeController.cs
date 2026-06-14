using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderHub.Infrastructure.Options;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    private readonly CustomerOnboardingOptions _onboardingOptions;
    private readonly IWebHostEnvironment _environment;

    public HomeController(IOptions<CustomerOnboardingOptions> onboardingOptions, IWebHostEnvironment environment)
    {
        _onboardingOptions = onboardingOptions.Value;
        _environment = environment;
    }

    public IActionResult Index()
    {
        return View();
    }

    [AllowAnonymous]
    [HttpGet("/customer-access-required")]
    public IActionResult CustomerAccessRequired([FromQuery] string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpGet("/tenant-not-found")]
    public IActionResult TenantNotFound([FromQuery] string? host = null)
    {
        ViewData["RequestedHost"] = host;
        ViewData["SignupUrl"] = BuildSignupUrl();
        ViewData["ShowDevDetails"] = _environment.IsDevelopment();
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
