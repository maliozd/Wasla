namespace Wasla.Application.Abstractions.Onboarding.Checkout;

public enum CheckoutSimulationOutcome
{
    Applied = 0,
    NotFound = 1,
    AlreadyPaymentSucceeded = 2,
    AlreadyPaymentFailed = 3,
    AlreadyCancelled = 4,
    AlreadyProvisioned = 5,
    AlreadyExpired = 6,
    InvalidState = 7
}
