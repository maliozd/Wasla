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
    /// Returns the most recent non-terminal PendingRegistration whose PrimaryDomain host matches
    /// the supplied host value, or null if none exists. The input is normalized to lowercase host
    /// only (scheme and port are stripped). Used by tenant-not-found to detect registrations that
    /// are awaiting payment, payment-received, or already provisioned.
    /// </summary>
    Task<PendingRegistrationSummary?> GetActiveByPrimaryDomainAsync(string primaryDomain, CancellationToken ct);
}
