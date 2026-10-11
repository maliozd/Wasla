namespace Wasla.Application.Abstractions.Onboarding.PendingRegistrations;

public enum PendingRegistrationError
{
    None = 0,
    InvalidSlug,
    DuplicateSlug,
    DuplicateDomain,
    DuplicateDatabaseName,
    InvalidPlan,
    SaveFailed,

    /// <summary>The owner email already has the maximum number of active (unexpired, unfinished) registrations.</summary>
    TooManyActiveRegistrations
}
