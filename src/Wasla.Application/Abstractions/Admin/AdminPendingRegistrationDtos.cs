using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Admin;

public enum PendingRegistrationAdminFilter
{
    All = 0,
    PendingPayment = 1,
    PaymentReceivedSetupPending = 2,
    Provisioned = 3
}

public sealed class AdminPendingRegistrationCountsDto
{
    public int Total { get; init; }
    public int PendingPayment { get; init; }
    public int PaymentReceivedSetupPending { get; init; }
    public int Provisioned { get; init; }
}

public sealed class AdminPendingRegistrationListItemDto
{
    public Guid Id { get; init; }
    public string BusinessName { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string OwnerFullName { get; init; } = string.Empty;
    public string OwnerEmail { get; init; } = string.Empty;
    public string PlanCode { get; init; } = string.Empty;
    public string BillingPeriod { get; init; } = string.Empty;
    public PendingRegistrationStatus Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? PaymentSucceededAtUtc { get; init; }
    public DateTime? ProvisionedAtUtc { get; init; }
    public Guid? TenantId { get; init; }
    public string? TenantDomain { get; init; }
    public bool IsEligibleForProvisioning { get; init; }
}

public sealed class AdminPendingRegistrationDetailDto
{
    public Guid Id { get; init; }
    public string BusinessName { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string PrimaryDomain { get; init; } = string.Empty;
    public string PlanCode { get; init; } = string.Empty;
    public string BillingPeriod { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string District { get; init; } = string.Empty;
    public string? Neighborhood { get; init; }
    public string? StreetAddress { get; init; }
    public string OwnerFullName { get; init; } = string.Empty;
    public string OwnerEmail { get; init; } = string.Empty;
    public string? OwnerPhone { get; init; }
    public string? BusinessPhone { get; init; }
    public string? BusinessEmail { get; init; }
    public PendingRegistrationStatus Status { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public DateTime? PaymentSucceededAtUtc { get; init; }
    public DateTime? PaymentFailedAtUtc { get; init; }
    public DateTime? ProvisionedAtUtc { get; init; }
    public Guid? TenantId { get; init; }
    public string? TenantDomain { get; init; }
    public bool IsEligibleForProvisioning { get; init; }
}

public sealed class AdminPendingRegistrationListResult
{
    public IReadOnlyList<AdminPendingRegistrationListItemDto> Items { get; init; } =
        Array.Empty<AdminPendingRegistrationListItemDto>();
    public PendingRegistrationAdminFilter AppliedFilter { get; init; }
}
