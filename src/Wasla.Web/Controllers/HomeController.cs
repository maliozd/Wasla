using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Options;
using Wasla.Web.Routing;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    private readonly TenantOnboardingOptions _onboardingOptions;
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IWebHostEnvironment _environment;

    public HomeController(
        IOptions<TenantOnboardingOptions> onboardingOptions,
        IPendingRegistrationService pendingRegistrations,
        IWebHostEnvironment environment)
    {
        _onboardingOptions = onboardingOptions.Value;
        _pendingRegistrations = pendingRegistrations;
        _environment = environment;
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
        ViewData["ExampleTenantAddress"] = RegistrationNameNormalizer.BuildPrimaryDomain(
            "bahce-tadi",
            _onboardingOptions.ApplicationBaseDomain);
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
    public async Task<IActionResult> TenantNotFound([FromQuery] string? host = null, CancellationToken ct = default)
    {
        // Only a tenant address is looked up (WAS-66): any other value in the query is shown, never queried.
        if (RegistrationNameNormalizer.ExtractSlugFromHost(host, _onboardingOptions.ApplicationBaseDomain) is not null)
        {
            var pending = await _pendingRegistrations.GetActiveByPrimaryDomainAsync(host!, ct);
            if (pending is not null)
                return Redirect($"/signup/pending/{pending.Id}");
        }

        ViewData["RequestedHost"] = host;
        ViewData["SignupUrl"] = BuildSignupUrl();
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View();
    }

    private string BuildSignupUrl() =>
        TenantWelcomeUrlBuilder.BuildCentralSignupUrl(Request, _environment, _onboardingOptions.MarketingBaseDomain);
}
