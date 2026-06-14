namespace OrderHub.Application.Abstractions.Onboarding;

using OrderHub.Domain.Enums;

public sealed record PendingRegistrationSummary(
    Guid Id,
    string BusinessName,
    string PrimaryDomain,
    string PlanCode,
    string BillingPeriod,
    PendingRegistrationStatus Status);
