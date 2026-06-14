namespace OrderHub.Application.Abstractions.Onboarding.Signup;

public sealed record CustomerSignupResult(
    bool Success,
    Guid? CustomerId,
    Guid? UserId,
    string? PrimaryDomain,
    string? Slug,
    CustomerSignupError Error = CustomerSignupError.None,
    string? ErrorMessage = null);
