namespace OrderHub.Application.Abstractions.Onboarding;

public enum CustomerSignupError
{
    None = 0,
    InvalidSlug,
    DuplicateSlug,
    DuplicateDomain,
    InvalidPlan,
    DatabaseProvisioningFailed
}
