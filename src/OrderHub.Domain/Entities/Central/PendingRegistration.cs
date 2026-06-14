using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Central;

/// <summary>
/// Pre-provisioning signup record. Created on public registration before payment and tenant provisioning.
/// </summary>
public class PendingRegistration
{
    public Guid Id { get; set; }

    public string PlanCode { get; set; } = default!;

    public string BillingPeriod { get; set; } = default!;

    public string BusinessName { get; set; } = default!;

    public string BusinessType { get; set; } = default!;

    public string Slug { get; set; } = default!;

    public string PrimaryDomain { get; set; } = default!;

    public string DatabaseName { get; set; } = default!;

    public string BusinessPhone { get; set; } = default!;

    public string Country { get; set; } = default!;

    public string City { get; set; } = default!;

    public string District { get; set; } = default!;

    public string? Neighborhood { get; set; }

    public string AddressLine1 { get; set; } = default!;

    public string? AddressLine2 { get; set; }

    public string? PostalCode { get; set; }

    public string OwnerFullName { get; set; } = default!;

    public string OwnerEmail { get; set; } = default!;

    public string? OwnerPhone { get; set; }

    public string PasswordHash { get; set; } = default!;

    public PendingRegistrationStatus Status { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public DateTime? PaymentSucceededAtUtc { get; set; }

    public DateTime? PaymentFailedAtUtc { get; set; }

    public string? SimulatedPaymentReference { get; set; }

    public Guid? CustomerId { get; set; }

    public DateTime? ProvisionedAtUtc { get; set; }
}
