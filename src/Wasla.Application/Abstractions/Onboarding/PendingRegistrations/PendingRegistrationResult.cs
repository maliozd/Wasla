namespace Wasla.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationResult(
    bool Success,
    Guid? RegistrationId,
    string? PrimaryDomain,
    string? Slug,
    string? DatabaseName,
    PendingRegistrationError Error = PendingRegistrationError.None,
    string? ErrorMessage = null);
