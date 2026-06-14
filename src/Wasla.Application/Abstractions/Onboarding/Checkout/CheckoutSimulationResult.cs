namespace Wasla.Application.Abstractions.Onboarding.Checkout;

public sealed record CheckoutSimulationResult(
    CheckoutSimulationOutcome Outcome,
    Guid? RegistrationId,
    string? SimulatedPaymentReference = null);
