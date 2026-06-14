using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationCheckoutDetails(
    Guid Id,
    string PlanCode,
    string BillingPeriod,
    string BusinessName,
    string PrimaryDomain,
    string BusinessPhone,
    string OwnerFullName,
    string OwnerEmail,
    string? OwnerPhone,
    string Country,
    string City,
    string District,
    string? Neighborhood,
    string AddressLine1,
    string? AddressLine2,
    string? PostalCode,
    PendingRegistrationStatus Status,
    decimal MonthlyPriceTry,
    decimal TotalPriceTry,
    bool IsYearlyBilling);
