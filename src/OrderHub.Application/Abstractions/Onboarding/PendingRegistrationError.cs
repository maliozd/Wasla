namespace OrderHub.Application.Abstractions.Onboarding;

public enum PendingRegistrationError
{
    None = 0,
    InvalidSlug,
    DuplicateSlug,
    DuplicateDomain,
    DuplicateDatabaseName,
    InvalidPlan,
    SaveFailed
}
