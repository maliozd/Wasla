namespace OrderHub.Application.Abstractions.Onboarding;

public sealed record CustomerSignupRequest(
    string BusinessName,
    string? BusinessType,
    string? Phone,
    string? City,
    string? Country,
    string Slug,
    string OwnerFullName,
    string OwnerEmail,
    string Password,
    string PlanCode,
    string BillingPeriod);
