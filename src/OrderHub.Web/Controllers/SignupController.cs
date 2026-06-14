using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Application.Abstractions.Signup;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Options;
using OrderHub.Web.Models.Signup;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
[Route("signup")]
public sealed class SignupController : Controller
{
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly ISignupReferenceDataService _referenceData;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly IValidator<PendingRegistrationRequest> _signupValidator;
    private readonly CustomerOnboardingOptions _options;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SignupController(
        IPendingRegistrationService pendingRegistrations,
        ISignupReferenceDataService referenceData,
        IOrderHubPlanCatalog planCatalog,
        IValidator<PendingRegistrationRequest> signupValidator,
        IOptions<CustomerOnboardingOptions> options,
        IStringLocalizer<SharedResource> localizer)
    {
        _pendingRegistrations = pendingRegistrations;
        _referenceData = referenceData;
        _planCatalog = planCatalog;
        _signupValidator = signupValidator;
        _options = options.Value;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] string? plan, CancellationToken ct)
    {
        var model = await CreateViewModelAsync(plan, ct);
        return View(model);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("")]
    public async Task<IActionResult> Index(SignupViewModel model, CancellationToken ct)
    {
        await PopulateReferenceDataAsync(model, ct);

        if (!ModelState.IsValid)
            return View(model);

        if (!_planCatalog.IsSelfServicePlan(model.PlanCode))
        {
            ModelState.AddModelError(nameof(model.PlanCode), _localizer["Signup.EnterpriseContactOnly"].Value);
            return View(model);
        }

        if (!await _pendingRegistrations.IsSlugAvailableAsync(model.Slug, ct))
        {
            ModelState.AddModelError(nameof(model.Slug), _localizer["Signup.SlugUnavailable"].Value);
            return View(model);
        }

        if (model.SelectedBusinessTypeCodes.Count == 0)
        {
            ModelState.AddModelError(nameof(model.SelectedBusinessTypeCodes), _localizer["Validation.BusinessTypeRequired"].Value);
            return View(model);
        }

        var cityName = model.City;
        var districtName = model.District;
        var neighborhoodName = model.Neighborhood;
        var streetAddress = model.StreetAddress;
        int? neighborhoodId = model.NeighborhoodId;
        int? streetId = model.StreetId;

        if (IsTurkey(model.Country) && model.CityId is int cityId && model.DistrictId is int districtId)
        {
            var resolved = await _referenceData.ResolveCityDistrictAsync(cityId, districtId, model.Country, ct);
            if (resolved is null)
            {
                ModelState.AddModelError(nameof(model.DistrictId), _localizer["Validation.CityDistrictInvalid"].Value);
                return View(model);
            }

            cityName = resolved.CityName;
            districtName = resolved.DistrictName;

            if (model.NeighborhoodId is int resolvedNeighborhoodId)
            {
                var neighborhood = await _referenceData.ResolveNeighborhoodAsync(districtId, resolvedNeighborhoodId, ct);
                if (neighborhood is null)
                {
                    ModelState.AddModelError(nameof(model.NeighborhoodId), _localizer["Validation.NeighborhoodInvalid"].Value);
                    return View(model);
                }

                neighborhoodName = neighborhood.NeighborhoodName;
                neighborhoodId = neighborhood.NeighborhoodId;

                if (model.StreetId is int resolvedStreetId)
                {
                    var street = await _referenceData.ResolveStreetAsync(resolvedNeighborhoodId, resolvedStreetId, ct);
                    if (street is null)
                    {
                        ModelState.AddModelError(nameof(model.StreetId), _localizer["Validation.StreetInvalid"].Value);
                        return View(model);
                    }

                    streetAddress = FormatStreetDisplay(street.StreetName, street.StreetType);
                    streetId = street.StreetId;
                }
            }
        }

        var request = new PendingRegistrationRequest(
            model.PlanCode,
            model.BillingPeriod,
            model.BusinessName,
            model.SelectedBusinessTypeCodes,
            model.BusinessPhoneType,
            model.BusinessPhone,
            model.BusinessEmail,
            model.Slug,
            model.Country,
            model.CityId,
            model.DistrictId,
            neighborhoodId,
            streetId,
            cityName,
            districtName,
            neighborhoodName,
            streetAddress,
            model.BuildingNumber,
            model.Floor,
            model.DoorNumber,
            model.AddressNote,
            model.PostalCode,
            model.LocationUrl,
            model.OwnerFullName,
            model.OwnerEmail,
            model.OwnerPhone,
            model.Password);

        var validation = await _signupValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                var field = MapValidationField(error.PropertyName);
                var message = LocalizeValidationMessage(error.ErrorMessage);
                ModelState.AddModelError(field, message);
            }

            return View(model);
        }

        var result = await _pendingRegistrations.SubmitAsync(request, ct);
        if (!result.Success)
        {
            ApplySignupError(result);
            return View(model);
        }

        return RedirectToAction(nameof(Pending), new { id = result.RegistrationId });
    }

    [HttpGet("districts")]
    public async Task<IActionResult> Districts([FromQuery] int cityId, CancellationToken ct)
    {
        if (cityId <= 0)
            return BadRequest();

        var districts = await _referenceData.GetDistrictsByCityIdAsync(cityId, ct);
        return Json(districts.Select(d => new { id = d.Id, name = d.Name }));
    }

    [HttpGet("neighborhoods")]
    public async Task<IActionResult> Neighborhoods([FromQuery] int districtId, CancellationToken ct)
    {
        if (districtId <= 0)
            return BadRequest();

        var neighborhoods = await _referenceData.GetNeighborhoodsByDistrictIdAsync(districtId, ct);
        return Json(neighborhoods.Select(n => new { id = n.Id, name = n.Name }));
    }

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

    [HttpGet("pending/{id:guid}")]
    public async Task<IActionResult> Pending(Guid id, CancellationToken ct)
    {
        var summary = await _pendingRegistrations.GetSummaryAsync(id, ct);
        if (summary is null)
            return NotFound();

        var plan = _planCatalog.FindByCode(summary.PlanCode);
        var planDisplay = plan is not null ? _localizer[plan.DisplayNameKey].Value : summary.PlanCode;

        return View(new SignupPendingViewModel
        {
            RegistrationId = summary.Id,
            BusinessName = summary.BusinessName,
            PrimaryDomain = summary.PrimaryDomain,
            PlanCode = summary.PlanCode,
            PlanDisplayName = planDisplay,
            BillingPeriod = summary.BillingPeriod
        });
    }

    private async Task<SignupViewModel> CreateViewModelAsync(string? plan, CancellationToken ct)
    {
        var normalized = _planCatalog.NormalizePlanCode(plan);
        var found = _planCatalog.FindByCode(normalized);
        var selected = found?.PlanCode ?? OrderHubPlanCodes.Starter;
        var model = new SignupViewModel
        {
            PlanCode = selected,
            IsContactSalesPlan = found?.IsContactSales ?? false,
            MarketingBaseDomain = _options.MarketingBaseDomain,
            PlanOptions = BuildPlanOptions(selected),
            Country = "Türkiye",
            BusinessPhoneType = "Mobile"
        };

        await PopulateReferenceDataAsync(model, ct);
        return model;
    }

    private async Task PopulateReferenceDataAsync(SignupViewModel model, CancellationToken ct)
    {
        model.MarketingBaseDomain = _options.MarketingBaseDomain;
        model.PlanOptions = BuildPlanOptions(model.PlanCode);
        model.IsContactSalesPlan = _planCatalog.FindByCode(model.PlanCode)?.IsContactSales ?? false;
        model.BusinessTypeOptions = await _referenceData.GetActiveBusinessTypesAsync(ct);

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

    private void ApplySignupError(PendingRegistrationResult result)
    {
        var message = result.Error switch
        {
            PendingRegistrationError.DuplicateSlug => _localizer["Signup.SlugUnavailable"].Value,
            PendingRegistrationError.DuplicateDomain => _localizer["Signup.SlugUnavailable"].Value,
            PendingRegistrationError.InvalidSlug => _localizer["Validation.SlugInvalid"].Value,
            PendingRegistrationError.InvalidPlan => _localizer["Validation.PlanInvalid"].Value,
            PendingRegistrationError.DuplicateDatabaseName => _localizer["Signup.ProvisioningFailed"].Value,
            _ => _localizer["Signup.ProvisioningFailed"].Value
        };

        if (result.Error is PendingRegistrationError.DuplicateSlug
            or PendingRegistrationError.DuplicateDomain
            or PendingRegistrationError.InvalidSlug)
        {
            ModelState.AddModelError(nameof(SignupViewModel.Slug), message);
        }
        else if (result.Error == PendingRegistrationError.InvalidPlan)
        {
            ModelState.AddModelError(nameof(SignupViewModel.PlanCode), message);
        }
        else
        {
            ModelState.AddModelError(string.Empty, message);
        }
    }

    private static string MapValidationField(string propertyName) => propertyName switch
    {
        nameof(PendingRegistrationRequest.BusinessName) => nameof(SignupViewModel.BusinessName),
        nameof(PendingRegistrationRequest.BusinessTypeCodes) => nameof(SignupViewModel.SelectedBusinessTypeCodes),
        nameof(PendingRegistrationRequest.BusinessPhoneType) => nameof(SignupViewModel.BusinessPhoneType),
        nameof(PendingRegistrationRequest.BusinessPhone) => nameof(SignupViewModel.BusinessPhone),
        nameof(PendingRegistrationRequest.BusinessEmail) => nameof(SignupViewModel.BusinessEmail),
        nameof(PendingRegistrationRequest.Slug) => nameof(SignupViewModel.Slug),
        nameof(PendingRegistrationRequest.Country) => nameof(SignupViewModel.Country),
        nameof(PendingRegistrationRequest.CityId) => nameof(SignupViewModel.CityId),
        nameof(PendingRegistrationRequest.DistrictId) => nameof(SignupViewModel.DistrictId),
        nameof(PendingRegistrationRequest.City) => nameof(SignupViewModel.City),
        nameof(PendingRegistrationRequest.District) => nameof(SignupViewModel.District),
        nameof(PendingRegistrationRequest.Neighborhood) => nameof(SignupViewModel.Neighborhood),
        nameof(PendingRegistrationRequest.NeighborhoodId) => nameof(SignupViewModel.NeighborhoodId),
        nameof(PendingRegistrationRequest.StreetId) => nameof(SignupViewModel.StreetId),
        nameof(PendingRegistrationRequest.StreetAddress) => nameof(SignupViewModel.StreetAddress),
        nameof(PendingRegistrationRequest.BuildingNumber) => nameof(SignupViewModel.BuildingNumber),
        nameof(PendingRegistrationRequest.Floor) => nameof(SignupViewModel.Floor),
        nameof(PendingRegistrationRequest.DoorNumber) => nameof(SignupViewModel.DoorNumber),
        nameof(PendingRegistrationRequest.AddressNote) => nameof(SignupViewModel.AddressNote),
        nameof(PendingRegistrationRequest.PostalCode) => nameof(SignupViewModel.PostalCode),
        nameof(PendingRegistrationRequest.LocationUrl) => nameof(SignupViewModel.LocationUrl),
        nameof(PendingRegistrationRequest.OwnerFullName) => nameof(SignupViewModel.OwnerFullName),
        nameof(PendingRegistrationRequest.OwnerEmail) => nameof(SignupViewModel.OwnerEmail),
        nameof(PendingRegistrationRequest.OwnerPhone) => nameof(SignupViewModel.OwnerPhone),
        nameof(PendingRegistrationRequest.Password) => nameof(SignupViewModel.Password),
        nameof(PendingRegistrationRequest.PlanCode) => nameof(SignupViewModel.PlanCode),
        nameof(PendingRegistrationRequest.BillingPeriod) => nameof(SignupViewModel.BillingPeriod),
        _ => string.Empty
    };

    private string LocalizeValidationMessage(string message)
    {
        var localized = _localizer[message];
        return !localized.ResourceNotFound ? localized.Value : message;
    }

    private static bool IsTurkey(string country) =>
        string.Equals(country.Trim(), "Türkiye", StringComparison.OrdinalIgnoreCase)
        || string.Equals(country.Trim(), "Turkey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(country.Trim(), "TR", StringComparison.OrdinalIgnoreCase);

    private static string FormatStreetDisplay(string name, string? streetType) =>
        string.IsNullOrWhiteSpace(streetType) ? name.Trim() : $"{name.Trim()} {streetType.Trim()}";
}
