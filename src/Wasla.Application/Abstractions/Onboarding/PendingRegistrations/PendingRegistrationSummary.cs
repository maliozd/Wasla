using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationSummary(
    Guid Id,
    string BusinessName,
    string PrimaryDomain,
    string PlanCode,
    string BillingPeriod,
    PendingRegistrationStatus Status,
    string BusinessPhone,
    string? BusinessEmail,
    string OwnerFullName,
    string OwnerEmail,
    string? OwnerPhone,
    DateTime CreatedAtUtc,
    DateTime? PaymentSucceededAtUtc,
    DateTime? ProvisionedAtUtc);
