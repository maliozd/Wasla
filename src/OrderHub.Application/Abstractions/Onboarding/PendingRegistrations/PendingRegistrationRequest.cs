namespace OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationRequest(
    string PlanCode,
    string BillingPeriod,
    string BusinessName,
    string BusinessType,
    string BusinessPhone,
    string Slug,
    string Country,
    string City,
    string District,
    string? Neighborhood,
    string AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    string OwnerFullName,
    string OwnerEmail,
    string? OwnerPhone,
    string Password);
