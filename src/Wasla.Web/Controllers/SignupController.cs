using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Billing;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Signup;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Sms;
using Wasla.Web.Models.Signup;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

/// <summary>
/// The signup page, its address lookups and the registration status page. The page saves its steps and submits through
/// the draft API (<see cref="SignupDraftController"/>, WAS-192); there is no form submission.
/// </summary>
[AllowAnonymous]
[Route("signup")]
public sealed class SignupController : Controller
{
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IRegistrationDraftService _drafts;
    private readonly IRegistrationPhoneVerificationService _phoneVerification;
    private readonly ISignupReferenceDataService _referenceData;
    private readonly IWaslaPlanCatalog _planCatalog;
    private readonly IBillingPlanPriceReader _planPrices;
    private readonly TenantOnboardingOptions _options;
    private readonly SignupOptions _signupOptions;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly SignupRegistrationOwnership _ownership;
    private readonly SignupDraftCookie _draftCookie;

    public SignupController(
        IPendingRegistrationService pendingRegistrations,
        IRegistrationDraftService drafts,
        IRegistrationPhoneVerificationService phoneVerification,
        ISignupReferenceDataService referenceData,
        IWaslaPlanCatalog planCatalog,
        IBillingPlanPriceReader planPrices,
        IOptions<TenantOnboardingOptions> options,
        IOptions<SignupOptions> signupOptions,
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment,
        IDataProtectionProvider dataProtection,
        TimeProvider time)
    {
        _pendingRegistrations = pendingRegistrations;
        _drafts = drafts;
        _phoneVerification = phoneVerification;
        _referenceData = referenceData;
        _planCatalog = planCatalog;
        _planPrices = planPrices;
        _options = options.Value;
        _signupOptions = signupOptions.Value;
        _localizer = localizer;
        _environment = environment;
        _time = time;
        _ownership = new SignupRegistrationOwnership(dataProtection, environment);
        _draftCookie = new SignupDraftCookie(dataProtection, environment);
    }

    // While Wasla:TenantOnboarding:PublicSignupEnabled is false, this page and the routes that serve only it (the draft
    // API and the address lookups) are closed; existing registrations keep their status and checkout pages (WAS-76).
    // The page embeds the browser's draft (personal data), so no cache may store it (WAS-192).
    [PublicSignupRequired(showUnavailablePage: true)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] string? plan, CancellationToken ct)
    {
        var model = await CreateViewModelAsync(plan, ct);
        return View(model);
    }

    [PublicSignupRequired]
    [HttpGet("districts")]
    public async Task<IActionResult> Districts([FromQuery] int cityId, CancellationToken ct)
    {
        if (cityId <= 0)
            return BadRequest();

        var districts = await _referenceData.GetDistrictsByCityIdAsync(cityId, ct);
        return Json(districts.Select(d => new { id = d.Id, name = d.Name }));
    }

    [PublicSignupRequired]
    [HttpGet("neighborhoods")]
    public async Task<IActionResult> Neighborhoods([FromQuery] int districtId, CancellationToken ct)
    {
        if (districtId <= 0)
            return BadRequest();

        var neighborhoods = await _referenceData.GetNeighborhoodsByDistrictIdAsync(districtId, ct);
        return Json(neighborhoods.Select(n => new { id = n.Id, name = n.Name }));
    }

    [PublicSignupRequired]
    [HttpGet("streets")]
    public async Task<IActionResult> Streets([FromQuery] int neighborhoodId, CancellationToken ct)
    {
        if (neighborhoodId <= 0)
            return BadRequest();

        var streets = await _referenceData.GetStreetsByNeighborhoodIdAsync(neighborhoodId, ct);
        return Json(streets.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            streetType = s.StreetType,
            displayName = FormatStreetDisplay(s.Name, s.StreetType)
        }));
    }

    // The same URL shows the applicant's details to their browser and only the status to anyone else,
    // so no cache may store a response for reuse.
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpGet("pending/{id:guid}")]
    public async Task<IActionResult> Pending(Guid id, CancellationToken ct)
    {
        var summary = await _pendingRegistrations.GetSummaryAsync(id, ct);
        if (summary is null)
            return NotFound();

        var plan = _planCatalog.FindByCode(summary.PlanCode);
        var planDisplay = plan is not null ? _localizer[plan.DisplayNameKey].Value : summary.PlanCode;

        // Anyone can open this page with the ID (the pending-tenant redirect reveals it), so only the
        // browser that submitted the signup sees its private details and the checkout action.
        return View(SignupPendingViewModelMapper.FromSummary(
            summary,
            planDisplay,
            Request,
            _environment,
            _options.MarketingBaseDomain,
            includePrivateDetails: _ownership.IsOwner(Request, id),
            showCheckoutAction: summary.Status is PendingRegistrationStatus.AwaitingPayment
                or PendingRegistrationStatus.Draft));
    }

    private async Task<SignupViewModel> CreateViewModelAsync(string? plan, CancellationToken ct)
    {
        var normalized = _planCatalog.NormalizePlanCode(plan);
        var found = _planCatalog.FindByCode(normalized);
        var selected = found?.PlanCode ?? WaslaPlanCodes.Starter;
        var model = new SignupViewModel
        {
            PlanCode = selected,
            IsContactSalesPlan = found?.IsContactSales ?? false,
            ApplicationBaseDomain = _options.ApplicationBaseDomain,
            PlanOptions = BuildPlanOptions(selected),
            Country = "Türkiye",
            BusinessPhoneType = "Mobile"
        };

        await PopulateReferenceDataAsync(model, ct);
        await PopulateDraftAsync(model, found?.PlanCode, ct);
        return model;
    }

    /// <summary>The browser's draft, the API URLs and the page facts the script needs (WAS-192).</summary>
    private async Task PopulateDraftAsync(SignupViewModel model, string? requestedPlanCode, CancellationToken ct)
    {
        var draftId = _draftCookie.Read(Request);
        var draft = draftId is { } id ? await _drafts.GetAsync(id, ct) : null;
        if (draft is null && SignupDraftCookie.IsPresent(Request))
            _draftCookie.Clear(HttpContext);

        var fakeOutbox = _environment.IsDevelopment() && HttpContext.RequestServices.GetService<FakeSmsOutbox>() is not null;
        model.InitialDraft = SignupDraftResponses.State(
            draft,
            _phoneVerification.IsAvailable,
            model.PlanPrices,
            _localizer,
            registrationId => Url.Action(nameof(Pending), new { id = registrationId })!,
            _time.GetUtcNow().UtcDateTime);
        model.Endpoints = SignupDraftResponses.Endpoints(Url, fakeOutbox);
        model.RequestedPlanCode = requestedPlanCode;
        model.IsPhoneVerificationAvailable = _phoneVerification.IsAvailable;
        model.PhoneCodeLength = PhoneVerificationPolicy.CodeLength;
        model.SupportEmail = _signupOptions.SupportEmail;
    }

    private async Task PopulateReferenceDataAsync(SignupViewModel model, CancellationToken ct)
    {
        model.ApplicationBaseDomain = _options.ApplicationBaseDomain;
        model.IsPaymentSimulatorAvailable = CheckoutPaymentSimulator.IsAvailable(_environment);
        model.PlanOptions = BuildPlanOptions(model.PlanCode);
        model.IsContactSalesPlan = _planCatalog.FindByCode(model.PlanCode)?.IsContactSales ?? false;
        // The prices checkout would charge now (WAS-147). A missing price is shown as not set; signup still works.
        model.PlanPrices = await _planPrices.GetPricesInEffectAsync(ct);
        model.BusinessTypeOptions = await _referenceData.GetActiveBusinessTypesAsync(ct);
        model.BusinessCategories = BusinessSubtypeCatalog.Categories
            .Select(category => new SignupBusinessCategoryGroup(
                category.ToString(),
                _localizer[BusinessSubtypeCatalog.ResourceKey(category)].Value,
                BusinessSubtypeCatalog.SubtypesOf(category)
                    .Select(subtype => new SignupBusinessSubtypeOption(
                        subtype.Code,
                        _localizer[BusinessSubtypeCatalog.ResourceKey(subtype.Subtype)].Value,
                        model.SelectedBusinessTypeCodes.Contains(subtype.Code, StringComparer.OrdinalIgnoreCase)))
                    .ToArray()))
            .ToArray();

        var cities = await _referenceData.GetActiveCitiesAsync(model.Country, ct);
        model.Cities = cities;
        model.CityOptions = cities
            .Select(c => new SelectListItem
            {
                Value = c.Id.ToString(),
                Text = c.Name,
                Selected = model.CityId == c.Id
            })
            .Prepend(new SelectListItem { Value = "", Text = _localizer["Signup.SelectCity"].Value })
            .ToList();

        if (model.CityId is int cityId)
        {
            var districts = await _referenceData.GetDistrictsByCityIdAsync(cityId, ct);
            model.DistrictOptions = districts
                .Select(d => new SelectListItem
                {
                    Value = d.Id.ToString(),
                    Text = d.Name,
                    Selected = model.DistrictId == d.Id
                })
                .Prepend(new SelectListItem { Value = "", Text = _localizer["Signup.SelectDistrict"].Value })
                .ToList();
        }
        else
        {
            model.DistrictOptions =
            [
                new SelectListItem { Value = "", Text = _localizer["Signup.SelectDistrict"].Value }
            ];
        }
    }

    private List<SelectListItem> BuildPlanOptions(string selected)
    {
        return _planCatalog.GetPublicPlans()
            .Select(p => new SelectListItem
            {
                Value = p.PlanCode,
                Text = _localizer[p.DisplayNameKey].Value,
                Selected = string.Equals(p.PlanCode, selected, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static string FormatStreetDisplay(string name, string? streetType) =>
        string.IsNullOrWhiteSpace(streetType) ? name.Trim() : $"{name.Trim()} {streetType.Trim()}";
}
