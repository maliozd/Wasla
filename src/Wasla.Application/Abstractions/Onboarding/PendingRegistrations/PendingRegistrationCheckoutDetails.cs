using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationCheckoutDetails(
    Guid Id,
    string PlanCode,
    string BillingPeriod,
    string BusinessName,
    string PrimaryDomain,
    string BusinessTypesDisplay,
    string BusinessPhone,
    string OwnerFullName,
    string OwnerEmail,
    string? OwnerPhone,
    string Country,
    string City,
    string District,
    string? Neighborhood,
    string? StreetAddress,
    string? BuildingNumber,
    string? Floor,
    string? DoorNumber,
    string? AddressNote,
    string? PostalCode,
    string? LocationUrl,
    PendingRegistrationStatus Status,
    decimal MonthlyPriceTry,
    decimal TotalPriceTry,
    bool IsYearlyBilling);
