using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Options;
using Wasla.Web.Models.Marketing;
using Wasla.Web.Routing;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
public sealed class HomeController : Controller
{
    /// <summary>The view shown at <c>/</c> while the full marketing site is not published.</summary>
    public const string ComingSoonViewName = "ComingSoon";

    private readonly TenantOnboardingOptions _onboardingOptions;
    private readonly MarketingSiteOptions _marketingSite;
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IWebHostEnvironment _environment;
    private readonly IBillingPlanPriceReader _planPrices;
    private readonly ILogger<HomeController> _logger;

    public HomeController(
        IOptions<TenantOnboardingOptions> onboardingOptions,
        IOptions<MarketingSiteOptions> marketingSite,
        IPendingRegistrationService pendingRegistrations,
        IWebHostEnvironment environment,
        IBillingPlanPriceReader planPrices,
        ILogger<HomeController> logger)
    {
        _onboardingOptions = onboardingOptions.Value;
        _marketingSite = marketingSite.Value;
        _pendingRegistrations = pendingRegistrations;
        _environment = environment;
        _planPrices = planPrices;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Until the full marketing site is published (Wasla:MarketingSite:FullSiteEnabled) the homepage is the
        // coming-soon page; it shows no prices, so nothing is read.
        if (!_marketingSite.FullSiteEnabled)
            return View(ComingSoonViewName);

        IReadOnlyList<BillingPriceQuote> prices;
        try
        {
            prices = await _planPrices.GetPricesInEffectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The marketing page did not depend on CentralDb before plan prices (WAS-147). If the prices cannot be read
            // it still renders and shows them as not available, never a built-in amount.
            _logger.LogWarning("Plan prices could not be read for the marketing page ({ExceptionType}).", ex.GetType().Name);
            prices = [];
        }

        return View(new MarketingHomeViewModel(prices));
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
