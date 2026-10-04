using Wasla.Application.Abstractions.Onboarding.PendingRegistrations;
using Wasla.Domain.Enums;
using Wasla.Web.Routing;

namespace Wasla.Web.Models.Signup;

public static class SignupPendingViewModelMapper
{
    /// <summary>
    /// Maps a registration for the status pages. Without <paramref name="includePrivateDetails"/> the
    /// model carries only the status and the tenant address, which the visitor already knows.
    /// </summary>
    public static SignupPendingViewModel FromSummary(
        PendingRegistrationSummary summary,
        string planDisplayName,
        HttpRequest request,
        IWebHostEnvironment environment,
        string marketingBaseDomain,
        bool includePrivateDetails,
        bool showCheckoutAction = false)
    {
        var hasPrimaryDomain = !string.IsNullOrWhiteSpace(summary.PrimaryDomain);

        var model = new SignupPendingViewModel
        {
            RegistrationId = summary.Id,
            PrimaryDomain = summary.PrimaryDomain,
            CentralHomepageUrl = TenantWelcomeUrlBuilder.BuildCentralHomepageUrl(
                request,
                environment,
                marketingBaseDomain),
            TenantAddressUrl = hasPrimaryDomain
                ? TenantWelcomeUrlBuilder.BuildTenantAddressUrl(
                    request,
                    environment,
                    summary.PrimaryDomain)
                : null,
            Status = summary.Status,
            PanelLoginUrl = summary.Status == PendingRegistrationStatus.Provisioned && hasPrimaryDomain
                ? TenantWelcomeUrlBuilder.BuildLoginUrl(request, environment, summary.PrimaryDomain)
                : null
        };

        if (!includePrivateDetails)
            return model;

        model.ShowPrivateDetails = true;
        model.ShowCheckoutAction = showCheckoutAction;
        model.ShowPaymentSimulatorNote = showCheckoutAction && environment.IsDevelopment();
        model.BusinessName = summary.BusinessName;
        model.PlanCode = summary.PlanCode;
        model.PlanDisplayName = planDisplayName;
        model.BillingPeriod = summary.BillingPeriod;
        model.BusinessPhone = summary.BusinessPhone;
        model.BusinessEmail = summary.BusinessEmail;
        model.OwnerFullName = summary.OwnerFullName;
        model.OwnerEmail = summary.OwnerEmail;
        model.OwnerPhone = summary.OwnerPhone;
        model.CreatedAtUtc = summary.CreatedAtUtc;
        model.PaymentSucceededAtUtc = summary.PaymentSucceededAtUtc;
        model.ProvisionedAtUtc = summary.ProvisionedAtUtc;
        return model;
    }
}
