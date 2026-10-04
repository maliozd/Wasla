using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Signup;

public sealed class SignupPendingViewModel
{
    public Guid RegistrationId { get; set; }

    public string PrimaryDomain { get; set; } = string.Empty;

    public string? TenantAddressUrl { get; set; }

    public string CentralHomepageUrl { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    public string PlanCode { get; set; } = string.Empty;

    public string PlanDisplayName { get; set; } = string.Empty;

    public string BillingPeriod { get; set; } = string.Empty;

    public PendingRegistrationStatus Status { get; set; }

    public string BusinessPhone { get; set; } = string.Empty;

    public string? BusinessEmail { get; set; }

    public string OwnerFullName { get; set; } = string.Empty;

    public string OwnerEmail { get; set; } = string.Empty;

    public string? OwnerPhone { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? PaymentSucceededAtUtc { get; set; }

    public DateTime? ProvisionedAtUtc { get; set; }

    public string? PanelLoginUrl { get; set; }

    public bool ShowCheckoutAction { get; set; }

    /// <summary>
    /// True only for the browser that proved it submitted this signup. Otherwise the business, plan,
    /// contact, and owner fields are left empty and only the public status is shown.
    /// </summary>
    public bool ShowPrivateDetails { get; set; }

    /// <summary>
    /// True only in Development, where checkout offers the payment simulator. Elsewhere the page must not
    /// suggest that payment can be simulated.
    /// </summary>
    public bool ShowPaymentSimulatorNote { get; set; }
}
