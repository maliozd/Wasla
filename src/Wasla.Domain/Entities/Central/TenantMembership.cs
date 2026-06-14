using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Minimal subscription/membership record for a tenant.
/// Billing provider integration is not implemented yet.
/// </summary>
public class TenantMembership : BaseEntity
{
    public Guid TenantId { get; set; }

    public Tenant Tenant { get; set; } = null!;

    public string PlanCode { get; set; } = string.Empty;

    public MembershipStatus Status { get; set; } = MembershipStatus.Trial;

    /// <summary>Monthly or Yearly placeholder for future billing.</summary>
    public string BillingPeriod { get; set; } = "Monthly";

    public DateTime StartedAt { get; set; }

    public DateTime? TrialEndsAt { get; set; }

    public DateTime? CurrentPeriodEndsAt { get; set; }

    public string? OwnerEmail { get; set; }

    public string? BusinessPhone { get; set; }

    public string? City { get; set; }

    public string? Country { get; set; }

    public string? BusinessType { get; set; }
}
