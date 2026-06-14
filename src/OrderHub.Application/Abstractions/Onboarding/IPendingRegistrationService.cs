namespace OrderHub.Application.Abstractions.Onboarding;

public interface IPendingRegistrationService
{
    Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct);

    Task<bool> IsDatabaseNameAvailableAsync(string databaseName, CancellationToken ct);

    Task<PendingRegistrationResult> SubmitAsync(PendingRegistrationRequest request, CancellationToken ct);

    Task<PendingRegistrationSummary?> GetSummaryAsync(Guid registrationId, CancellationToken ct);

    Task<PendingRegistrationCheckoutDetails?> GetCheckoutDetailsAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> SimulatePaymentSuccessAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> SimulatePaymentFailedAsync(Guid registrationId, CancellationToken ct);

    Task<CheckoutSimulationResult> CancelRegistrationAsync(Guid registrationId, CancellationToken ct);
}
