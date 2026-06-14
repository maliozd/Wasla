using OrderHub.Application.Abstractions.Onboarding.Signup;

namespace OrderHub.Application.Abstractions.Onboarding;

public interface ICustomerOnboardingService
{
    Task<bool> IsSlugAvailableAsync(string slug, CancellationToken ct);

    Task<CustomerSignupResult> RegisterAsync(CustomerSignupRequest request, CancellationToken ct);
}
