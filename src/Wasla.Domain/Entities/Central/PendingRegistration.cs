using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Pre-provisioning signup record. Created on public registration before payment and tenant provisioning.
/// </summary>
public class PendingRegistration
{
    public Guid Id { get; set; }

    public string PlanCode { get; set; } = default!;

    public string BillingPeriod { get; set; } = default!;

    public string BusinessName { get; set; } = default!;

    /// <summary>Deprecated display snapshot; use <see cref="BusinessTypes"/> for new registrations.</summary>
    public string? BusinessType { get; set; }

    public BusinessPhoneType BusinessPhoneType { get; set; }

    public string Slug { get; set; } = default!;

    public string PrimaryDomain { get; set; } = default!;

    public string DatabaseName { get; set; } = default!;

    public string BusinessPhone { get; set; } = default!;

    public string? BusinessEmail { get; set; }

    public string Country { get; set; } = default!;

    public int? CityId { get; set; }

    public int? DistrictId { get; set; }

    public int? NeighborhoodId { get; set; }

    public int? StreetId { get; set; }

    public string City { get; set; } = default!;

    public string District { get; set; } = default!;

    public string? Neighborhood { get; set; }

    public string? StreetAddress { get; set; }

    public string? BuildingNumber { get; set; }

    public string? Floor { get; set; }

    public string? DoorNumber { get; set; }

    public string? AddressNote { get; set; }

    public string? PostalCode { get; set; }

    public string? LocationUrl { get; set; }

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

    public Guid? TenantId { get; set; }

    public DateTime? ProvisionedAtUtc { get; set; }

    public ICollection<PendingRegistrationBusinessType> BusinessTypes { get; set; } = new List<PendingRegistrationBusinessType>();
}
