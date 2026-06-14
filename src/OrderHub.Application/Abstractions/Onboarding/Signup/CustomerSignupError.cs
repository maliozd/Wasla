namespace OrderHub.Application.Abstractions.Onboarding.Signup;

public enum CustomerSignupError
{
    None = 0,
    InvalidSlug,
    DuplicateSlug,
    DuplicateDomain,
    InvalidPlan,
    DatabaseProvisioningFailed
}
