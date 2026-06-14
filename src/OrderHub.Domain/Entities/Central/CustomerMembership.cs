using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Central;

/// <summary>
/// Minimal subscription/membership record for a customer tenant.
/// Billing provider integration is not implemented yet.
/// </summary>
public class CustomerMembership : BaseEntity
{
    public Guid CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

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
