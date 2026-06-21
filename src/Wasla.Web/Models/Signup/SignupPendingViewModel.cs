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
}
