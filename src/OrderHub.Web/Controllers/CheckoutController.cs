using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OrderHub.Application.Abstractions.Onboarding.Checkout;
using OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;
using OrderHub.Application.Abstractions.Plans;
using OrderHub.Domain.Enums;
using OrderHub.Web.Models.Checkout;

namespace OrderHub.Web.Controllers;

[AllowAnonymous]
[Route("checkout")]
public sealed class CheckoutController : Controller
{
    private readonly IPendingRegistrationService _pendingRegistrations;
    private readonly IOrderHubPlanCatalog _planCatalog;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CheckoutController(
        IPendingRegistrationService pendingRegistrations,
        IOrderHubPlanCatalog planCatalog,
        IStringLocalizer<SharedResource> localizer)
    {
        _pendingRegistrations = pendingRegistrations;
        _planCatalog = planCatalog;
        _localizer = localizer;
    }

    [HttpGet("review/{id:guid}")]
    public async Task<IActionResult> Review(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        return View(MapReviewViewModel(details));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("simulate-success/{id:guid}")]
    public async Task<IActionResult> SimulateSuccess(Guid id, CancellationToken ct)
    {
        var result = await _pendingRegistrations.SimulatePaymentSuccessAsync(id, ct);
        return HandleSimulationResult(result, nameof(Success));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("simulate-failed/{id:guid}")]
    public async Task<IActionResult> SimulateFailed(Guid id, CancellationToken ct)
    {
        var result = await _pendingRegistrations.SimulatePaymentFailedAsync(id, ct);
        return HandleSimulationResult(result, nameof(Failed));
    }

    [ValidateAntiForgeryToken]
    [HttpPost("cancel/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var result = await _pendingRegistrations.CancelRegistrationAsync(id, ct);
        return HandleSimulationResult(result, nameof(Cancelled));
    }

    [HttpGet("success/{id:guid}")]
    public async Task<IActionResult> Success(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        if (details.Status is not (PendingRegistrationStatus.PaymentSucceeded or PendingRegistrationStatus.Provisioned))
            return RedirectToAction(nameof(Review), new { id });

        return View(new CheckoutResultViewModel
        {
            RegistrationId = details.Id,
            BusinessName = details.BusinessName,
            PrimaryDomain = details.PrimaryDomain
        });
    }

    [HttpGet("failed/{id:guid}")]
    public async Task<IActionResult> Failed(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        if (details.Status != PendingRegistrationStatus.PaymentFailed)
            return RedirectToAction(nameof(Review), new { id });

        return View(new CheckoutResultViewModel
        {
            RegistrationId = details.Id,
            BusinessName = details.BusinessName,
            PrimaryDomain = details.PrimaryDomain
        });
    }

    [HttpGet("cancelled/{id:guid}")]
    public async Task<IActionResult> Cancelled(Guid id, CancellationToken ct)
    {
        var details = await _pendingRegistrations.GetCheckoutDetailsAsync(id, ct);
        if (details is null)
            return NotFound();

        if (details.Status != PendingRegistrationStatus.Cancelled)
            return RedirectToAction(nameof(Review), new { id });

        return View(new CheckoutResultViewModel
        {
            RegistrationId = details.Id,
            BusinessName = details.BusinessName,
            PrimaryDomain = details.PrimaryDomain
        });
    }

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

        var canSimulate = details.Status is PendingRegistrationStatus.AwaitingPayment
            or PendingRegistrationStatus.PaymentFailed;
        var canCancel = canSimulate;

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

    private static string BuildAddressSummary(PendingRegistrationCheckoutDetails details)
    {
        var parts = new List<string> { details.AddressLine1.Trim() };

        if (!string.IsNullOrWhiteSpace(details.AddressLine2))
            parts.Add(details.AddressLine2.Trim());

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
