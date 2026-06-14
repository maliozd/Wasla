using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Infrastructure.Options;
using OrderHub.Web.Models.Signup;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
[Route("signup")]
public sealed class SignupController : Controller
{
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly IValidator<PendingRegistrationRequest> _signupValidator;
    private readonly CustomerOnboardingOptions _options;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SignupController(
        IPendingRegistrationService pendingRegistrations,
        IOrderHubPlanCatalog planCatalog,
        IValidator<PendingRegistrationRequest> signupValidator,
        IOptions<CustomerOnboardingOptions> options,
        IStringLocalizer<SharedResource> localizer)
    {
        _pendingRegistrations = pendingRegistrations;
        _planCatalog = planCatalog;
        _signupValidator = signupValidator;
        _options = options.Value;
        _localizer = localizer;
    }

    [HttpGet("")]
    public IActionResult Index([FromQuery] string? plan)
    {
        var model = CreateViewModel(plan);
        return View(model);
    }

    [ValidateAntiForgeryToken]
    [HttpPost("")]
    public async Task<IActionResult> Index(SignupViewModel model, CancellationToken ct)
    {
        model.MarketingBaseDomain = _options.MarketingBaseDomain;
        model.PlanOptions = BuildPlanOptions(model.PlanCode);
        model.IsContactSalesPlan = _planCatalog.FindByCode(model.PlanCode)?.IsContactSales ?? false;

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

        var request = new PendingRegistrationRequest(
            model.PlanCode,
            model.BillingPeriod,
            model.BusinessName,
            model.BusinessType,
            model.BusinessPhone,
            model.Slug,
            model.Country,
            model.City,
            model.District,
            model.Neighborhood,
            model.AddressLine1,
            model.AddressLine2,
            model.PostalCode,
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

    private SignupViewModel CreateViewModel(string? plan)
    {
        var normalized = _planCatalog.NormalizePlanCode(plan);
        var found = _planCatalog.FindByCode(normalized);
        var selected = found?.PlanCode ?? OrderHubPlanCodes.Starter;
        return new SignupViewModel
        {
            PlanCode = selected,
            IsContactSalesPlan = found?.IsContactSales ?? false,
            MarketingBaseDomain = _options.MarketingBaseDomain,
            PlanOptions = BuildPlanOptions(selected),
            Country = "Türkiye"
        };
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
        nameof(PendingRegistrationRequest.BusinessType) => nameof(SignupViewModel.BusinessType),
        nameof(PendingRegistrationRequest.BusinessPhone) => nameof(SignupViewModel.BusinessPhone),
        nameof(PendingRegistrationRequest.Slug) => nameof(SignupViewModel.Slug),
        nameof(PendingRegistrationRequest.Country) => nameof(SignupViewModel.Country),
        nameof(PendingRegistrationRequest.City) => nameof(SignupViewModel.City),
        nameof(PendingRegistrationRequest.District) => nameof(SignupViewModel.District),
        nameof(PendingRegistrationRequest.Neighborhood) => nameof(SignupViewModel.Neighborhood),
        nameof(PendingRegistrationRequest.AddressLine1) => nameof(SignupViewModel.AddressLine1),
        nameof(PendingRegistrationRequest.AddressLine2) => nameof(SignupViewModel.AddressLine2),
        nameof(PendingRegistrationRequest.PostalCode) => nameof(SignupViewModel.PostalCode),
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
}
