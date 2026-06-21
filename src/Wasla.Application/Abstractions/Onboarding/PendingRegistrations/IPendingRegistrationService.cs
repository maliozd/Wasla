using Wasla.Application.Abstractions.Onboarding.Checkout;

namespace Wasla.Application.Abstractions.Onboarding.PendingRegistrations;

public interface IPendingRegistrationService
{
    Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct);

    Task<PendingRegistrationResult> SubmitAsync(PendingRegistrationRequest request, CancellationToken ct);

    Task<PendingRegistrationSummary?> GetSummaryAsync(Guid registrationId, CancellationToken ct);

    Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> SimulatePaymentSuccessAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> SimulatePaymentFailedAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> CancelRegistrationAsync(Guid registrationId, CancellationToken ct);

    /// <summary>
    /// Returns the most recent active/relevant PendingRegistration whose PrimaryDomain host or
    /// tenant slug matches the supplied host value, or null if none exists. The input is normalized
    /// to lowercase host only (scheme and port are stripped). Cancelled, failed, expired, and
    /// time-expired registrations are excluded.
    /// </summary>
    Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct);
}
