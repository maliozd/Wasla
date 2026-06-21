using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Domain.Enums;
using Wasla.Web.Routing;

namespace Wasla.Web.Models.Signup;

public static class SignupPendingViewModelMapper
{
    public static SignupPendingViewModel FromSummary(
        PendingRegistrationSummary summary,
        string planDisplayName,
        HttpRequest request,
        IWebHostEnvironment environment,
        bool showCheckoutAction = false)
    {
        var hasPrimaryDomain = !string.IsNullOrWhiteSpace(summary.PrimaryDomain);

        return new SignupPendingViewModel
        {
            RegistrationId = summary.Id,
            BusinessName = summary.BusinessName,
            PrimaryDomain = summary.PrimaryDomain,
            TenantAddressUrl = hasPrimaryDomain
                ? TenantWelcomeUrlBuilder.BuildTenantAddressUrl(
                    request,
                    environment,
                    summary.PrimaryDomain)
                : null,
            PlanCode = summary.PlanCode,
            PlanDisplayName = planDisplayName,
            BillingPeriod = summary.BillingPeriod,
            Status = summary.Status,
            BusinessPhone = summary.BusinessPhone,
            BusinessEmail = summary.BusinessEmail,
            OwnerFullName = summary.OwnerFullName,
            OwnerEmail = summary.OwnerEmail,
            OwnerPhone = summary.OwnerPhone,
            CreatedAtUtc = summary.CreatedAtUtc,
            PaymentSucceededAtUtc = summary.PaymentSucceededAtUtc,
            ProvisionedAtUtc = summary.ProvisionedAtUtc,
            PanelLoginUrl = summary.Status == PendingRegistrationStatus.Provisioned && hasPrimaryDomain
                ? TenantWelcomeUrlBuilder.BuildLoginUrl(request, environment, summary.PrimaryDomain)
                : null,
            ShowCheckoutAction = showCheckoutAction
        };
    }
}
