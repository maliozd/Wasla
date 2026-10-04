using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Onboarding.Checkout;
using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Application.Abstractions.Plans;
using Wasla.Application.Signup;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Options;
using Wasla.Web.Models.Checkout;
using Wasla.Web.Models.Signup;
using Wasla.Web.Security;

namespace Wasla.Web.Controllers;

[AllowAnonymous]
[Route("checkout")]
public sealed class CheckoutController : Controller
{
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IWaslaPlanCatalog _planCatalog;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IWebHostEnvironment _environment;
    private readonly CustomerOnboardingOptions _onboardingOptions;
    private readonly SignupRegistrationOwnership _ownership;

    public CheckoutController(
        IPendingRegistrationService pendingRegistrations,
        IWaslaPlanCatalog planCatalog,
        IStringLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment,
        IOptions<CustomerOnboardingOptions> onboardingOptions,
        IDataProtectionProvider dataProtection)
    {
        _pendingRegistrations = pendingRegistrations;
        _planCatalog = planCatalog;
        _localizer = localizer;
        _environment = environment;
        _onboardingOptions = onboardingOptions.Value;
        _ownership = new SignupRegistrationOwnership(dataProtection, environment);
    }

    // A registration ID is not secret (the pending-tenant redirect reveals it). Private pages and every
    // registration change require the ownership proof issued by the signup submission; anyone else is
    // sent to the public status page or gets 404.

    // Private pages: no cache may keep the applicant's details for another request.
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpGet("review/{id:guid}")]
    public async Task<IActionResult> Review(Guid id, CancellationToken ct)
    {
        if (!IsOwner(id))
            return RedirectToPublicStatus(id);

        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        return View(MapReviewViewModel(details));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("simulate-success/{id:guid}")]
    public async Task<IActionResult> SimulateSuccess(Guid id, CancellationToken ct)
    {
        if (!IsPaymentSimulatorAvailable || !IsOwner(id))
            return NotFound();

        var result = await _pendingRegistrations.SimulatePaymentSuccessAsync(id, ct);
        return HandleSimulationResult(result, nameof(Success));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("simulate-failed/{id:guid}")]
    public async Task<IActionResult> SimulateFailed(Guid id, CancellationToken ct)
    {
        if (!IsPaymentSimulatorAvailable || !IsOwner(id))
            return NotFound();

        var result = await _pendingRegistrations.SimulatePaymentFailedAsync(id, ct);
        return HandleSimulationResult(result, nameof(Failed));
    }

    // Cancelling releases the slug, so without ownership proof it must not change anything.
    [ValidateAntiForgeryToken]
    [HttpPost("cancel/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!IsOwner(id))
            return NotFound();

        var result = await _pendingRegistrations.CancelRegistrationAsync(id, ct);
        return HandleSimulationResult(result, nameof(Cancelled));
    }

    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpGet("success/{id:guid}")]
    public async Task<IActionResult> Success(Guid id, CancellationToken ct)
    {
        if (!IsOwner(id))
            return RedirectToPublicStatus(id);

        var summary = await _pendingRegistrations.GetSummaryAsync(id, ct);
        if (summary is null)
            return NotFound();

        if (summary.Status is not (PendingRegistrationStatus.PaymentSucceeded or PendingRegistrationStatus.Provisioned))
            return RedirectToAction(nameof(Review), new { id });

        var plan = _planCatalog.FindByCode(summary.PlanCode);
        var planDisplay = plan is not null
            ? _localizer[plan.DisplayNameKey].Value
            : summary.PlanCode;

        return View(SignupPendingViewModelMapper.FromSummary(
            summary,
            planDisplay,
            Request,
            _environment,
            _onboardingOptions.MarketingBaseDomain,
            includePrivateDetails: true));
    }

    [HttpGet("failed/{id:guid}")]
    public async Task<IActionResult> Failed(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        if (details.Status != PendingRegistrationStatus.PaymentFailed)
            return RedirectToAction(nameof(Review), new { id });

        // Public page: it carries only the ID and shows generic text.
        return View(new CheckoutResultViewModel { RegistrationId = details.Id });
    }

    [HttpGet("cancelled/{id:guid}")]
    public async Task<IActionResult> Cancelled(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        if (details.Status != PendingRegistrationStatus.Cancelled)
            return RedirectToAction(nameof(Review), new { id });

        // Public page: it carries only the ID and shows generic text.
        return View(new CheckoutResultViewModel { RegistrationId = details.Id });
    }

    // The payment simulator is a local-development tool. Anywhere else it would let an anonymous
    // request mark a registration as paid, which makes it eligible for provisioning.
    private bool IsPaymentSimulatorAvailable => _environment.IsDevelopment();

    private bool IsOwner(Guid registrationId) => _ownership.IsOwner(Request, registrationId);

    private RedirectToActionResult RedirectToPublicStatus(Guid registrationId) =>
        RedirectToAction(nameof(SignupController.Pending), "Signup", new { id = registrationId });

    private IActionResult HandleSimulationResult(CheckoutSimulationResult result, string targetAction)
    {
        var id = result.RegistrationId;

        return result.Outcome switch
        {
            CheckoutSimulationOutcome.NotFound => NotFound(),
            CheckoutSimulationOutcome.Applied when id.HasValue => RedirectToAction(targetAction, new { id = id.Value }),
            CheckoutSimulationOutcome.AlreadyPaymentSucceeded when id.HasValue => RedirectToAction(
                nameof(Success), new { id = id.Value }),
            CheckoutSimulationOutcome.AlreadyPaymentFailed when id.HasValue => RedirectToAction(
                nameof(Failed), new { id = id.Value }),
            CheckoutSimulationOutcome.AlreadyCancelled when id.HasValue => RedirectToAction(
                nameof(Cancelled), new { id = id.Value }),
            CheckoutSimulationOutcome.AlreadyProvisioned when id.HasValue => RedirectToAction(
                nameof(Success), new { id = id.Value }),
            CheckoutSimulationOutcome.AlreadyExpired when id.HasValue => RedirectToAction(
                nameof(Review), new { id = id.Value }),
            CheckoutSimulationOutcome.InvalidState when id.HasValue => RedirectToAction(
                nameof(Review), new { id = id.Value }),
            _ => NotFound()
        };
    }

    private CheckoutReviewViewModel MapReviewViewModel(PendingRegistrationCheckoutDetails details)
    {
        var plan = _planCatalog.FindByCode(details.PlanCode);
        var planDisplay = plan is not null
            ? _localizer[plan.DisplayNameKey].Value
            : _localizer["Checkout.UnknownPlanLabel"].Value;

        // Only the proven applicant reaches this view; the service re-applies the status rules on submit.
        var canCancel = details.Status is PendingRegistrationStatus.AwaitingPayment
            or PendingRegistrationStatus.PaymentFailed;
        var canSimulate = canCancel && IsPaymentSimulatorAvailable;

        string? statusNoticeKey = details.Status switch
        {
            PendingRegistrationStatus.PaymentSucceeded => "Checkout.StatusAlreadyPaid",
            PendingRegistrationStatus.Cancelled => "Checkout.StatusCancelled",
            PendingRegistrationStatus.Provisioned => "Checkout.StatusProvisioned",
            PendingRegistrationStatus.Expired => "Checkout.StatusExpired",
            _ => null
        };

        return new CheckoutReviewViewModel
        {
            RegistrationId = details.Id,
            PlanCode = details.PlanCode,
            PlanDisplayName = planDisplay,
            BillingPeriod = details.BillingPeriod,
            BusinessName = details.BusinessName,
            BusinessTypesDisplay = LocalizeBusinessTypes(details),
            PrimaryDomain = details.PrimaryDomain,
            BusinessPhone = details.BusinessPhone,
            OwnerFullName = details.OwnerFullName,
            OwnerEmail = details.OwnerEmail,
            OwnerPhone = details.OwnerPhone,
            AddressSummary = BuildAddressSummary(details),
            MonthlyPriceTry = details.MonthlyPriceTry,
            TotalPriceTry = details.TotalPriceTry,
            IsYearlyBilling = details.IsYearlyBilling,
            Status = details.Status,
            CanSimulatePayment = canSimulate,
            CanCancel = canCancel,
            StatusNoticeKey = statusNoticeKey
        };
    }

    private string LocalizeBusinessTypes(PendingRegistrationCheckoutDetails details)
    {
        if (details.BusinessTypeCodes is not { Count: > 0 })
            return details.BusinessTypesDisplay;

        var labels = new List<string>();
        foreach (var code in details.BusinessTypeCodes)
        {
            var key = BusinessSubtypeCatalog.ResourceKeyForCode(code);
            if (key is null)
            {
                labels.Add(code);
                continue;
            }

            var localized = _localizer[key].Value;
            labels.Add(string.IsNullOrWhiteSpace(localized) ? code : localized);
        }

        return labels.Count == 0 ? details.BusinessTypesDisplay : string.Join(", ", labels);
    }

    private static string BuildAddressSummary(PendingRegistrationCheckoutDetails details)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(details.StreetAddress))
            parts.Add(details.StreetAddress.Trim());

        if (!string.IsNullOrWhiteSpace(details.BuildingNumber))
            parts.Add($"No: {details.BuildingNumber.Trim()}");

        if (!string.IsNullOrWhiteSpace(details.Floor))
            parts.Add($"Kat: {details.Floor.Trim()}");

        if (!string.IsNullOrWhiteSpace(details.DoorNumber))
            parts.Add($"Daire: {details.DoorNumber.Trim()}");

        if (!string.IsNullOrWhiteSpace(details.AddressNote))
            parts.Add(details.AddressNote.Trim());

        if (!string.IsNullOrWhiteSpace(details.Neighborhood))
            parts.Add(details.Neighborhood.Trim());

        parts.Add(details.District.Trim());
        parts.Add(details.City.Trim());
        parts.Add(details.Country.Trim());

        if (!string.IsNullOrWhiteSpace(details.PostalCode))
            parts.Add(details.PostalCode.Trim());

        return string.Join(", ", parts);
    }
}
