using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OrderHub.Application.Abstractions.Auth;
using OrderHub.Application.Abstractions.Onboarding;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Options;
using OrderHub.Web.Models.Signup;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
[Route("signup")]
public sealed class SignupController : Controller
{
    private readonly ICustomerOnboardingService _onboarding;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly ISignupCompletionTokenService _completionTokens;
    private readonly IValidator<CustomerSignupRequest> _signupValidator;
    private readonly CustomerOnboardingOptions _options;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SignupController(
        ICustomerOnboardingService onboarding,
        IOrderHubPlanCatalog planCatalog,
        ISignupCompletionTokenService completionTokens,
        IValidator<CustomerSignupRequest> signupValidator,
        IOptions<CustomerOnboardingOptions> options,
        IStringLocalizer<SharedResource> localizer)
    {
        _onboarding = onboarding;
        _planCatalog = planCatalog;
        _completionTokens = completionTokens;
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

        if (!await _onboarding.IsSlugAvailableAsync(model.Slug, ct))
        {
            ModelState.AddModelError(nameof(model.Slug), _localizer["Signup.SlugUnavailable"].Value);
            return View(model);
        }

        var request = new CustomerSignupRequest(
            model.BusinessName,
            model.BusinessType,
            model.Phone,
            model.City,
            model.Country,
            model.Slug,
            model.OwnerFullName,
            model.OwnerEmail,
            model.Password,
            model.PlanCode,
            model.BillingPeriod);

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

        var result = await _onboarding.RegisterAsync(request, ct);
        if (!result.Success)
        {
            ApplySignupError(result);
            return View(model);
        }

        var token = _completionTokens.CreateToken(new SignupCompletionPayload(
            result.CustomerId!.Value,
            result.UserId!.Value,
            model.OwnerEmail.Trim(),
            model.OwnerFullName.Trim(),
            UserRole.Owner));

        var scheme = Request.IsHttps ? "https" : "http";
        var welcomeUrl = $"{scheme}://{result.PrimaryDomain}/auth/welcome?token={Uri.EscapeDataString(token)}";
        return Redirect(welcomeUrl);
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
            PlanOptions = BuildPlanOptions(selected)
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

    private void ApplySignupError(CustomerSignupResult result)
    {
        var message = result.Error switch
        {
            CustomerSignupError.DuplicateSlug => _localizer["Signup.SlugUnavailable"].Value,
            CustomerSignupError.DuplicateDomain => _localizer["Signup.SlugUnavailable"].Value,
            CustomerSignupError.InvalidSlug => _localizer["Validation.SlugInvalid"].Value,
            CustomerSignupError.InvalidPlan => _localizer["Validation.PlanInvalid"].Value,
            CustomerSignupError.DatabaseProvisioningFailed => _localizer["Signup.ProvisioningFailed"].Value,
            _ => _localizer["Signup.ProvisioningFailed"].Value
        };

        if (result.Error is CustomerSignupError.DuplicateSlug or CustomerSignupError.DuplicateDomain or CustomerSignupError.InvalidSlug)
            ModelState.AddModelError(nameof(SignupViewModel.Slug), message);
        else if (result.Error == CustomerSignupError.InvalidPlan)
            ModelState.AddModelError(nameof(SignupViewModel.PlanCode), message);
        else
            ModelState.AddModelError(string.Empty, message);
    }

    private static string MapValidationField(string propertyName) => propertyName switch
    {
        nameof(CustomerSignupRequest.BusinessName) => nameof(SignupViewModel.BusinessName),
        nameof(CustomerSignupRequest.Slug) => nameof(SignupViewModel.Slug),
        nameof(CustomerSignupRequest.OwnerFullName) => nameof(SignupViewModel.OwnerFullName),
        nameof(CustomerSignupRequest.OwnerEmail) => nameof(SignupViewModel.OwnerEmail),
        nameof(CustomerSignupRequest.Password) => nameof(SignupViewModel.Password),
        nameof(CustomerSignupRequest.PlanCode) => nameof(SignupViewModel.PlanCode),
        nameof(CustomerSignupRequest.BillingPeriod) => nameof(SignupViewModel.BillingPeriod),
        _ => string.Empty
    };

    private string LocalizeValidationMessage(string message)
    {
        var localized = _localizer[message];
        return !localized.ResourceNotFound ? localized.Value : message;
    }
}
