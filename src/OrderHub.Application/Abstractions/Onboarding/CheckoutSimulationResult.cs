namespace OrderHub.Application.Abstractions.Onboarding;

public sealed record CheckoutSimulationResult(
    CheckoutSimulationOutcome Outcome,
    Guid? RegistrationId,
    string? SimulatedPaymentReference = null);
