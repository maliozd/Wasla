using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationSummary(
    Guid Id,
    string BusinessName,
    string PrimaryDomain,
    string PlanCode,
    string BillingPeriod,
    PendingRegistrationStatus Status);
