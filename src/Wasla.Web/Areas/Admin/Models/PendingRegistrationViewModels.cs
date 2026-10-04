using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Enums;

namespace Wasla.Web.Areas.Admin.Models;

public sealed class AdminPendingRegistrationListViewModel
{
    public IReadOnlyList<AdminPendingRegistrationListItemDto> Items { get; set; } =
        Array.Empty<AdminPendingRegistrationListItemDto>();
    public PendingRegistrationAdminFilter ActiveFilter { get; set; }
}

public sealed class AdminPendingRegistrationDetailViewModel
{
    public Guid Id { get; set; }
    public string BusinessName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public string PlanCode { get; set; } = string.Empty;
    public string BillingPeriod { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string? Neighborhood { get; set; }
    public string? StreetAddress { get; set; }
    public string OwnerFullName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string? OwnerPhone { get; set; }
    public string? BusinessPhone { get; set; }
    public string? BusinessEmail { get; set; }
    public PendingRegistrationStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? PaymentSucceededAtUtc { get; set; }
    public DateTime? PaymentFailedAtUtc { get; set; }
    public DateTime? ProvisionedAtUtc { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantDomain { get; set; }
    public bool IsEligibleForProvisioning { get; set; }

    /// <summary>Non-null after a provision attempt in the same request-response cycle.</summary>
    public string? ProvisionResultMessage { get; set; }
    public bool? ProvisionSucceeded { get; set; }
}
