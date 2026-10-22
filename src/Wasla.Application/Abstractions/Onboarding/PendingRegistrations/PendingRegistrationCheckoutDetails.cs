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
    BillingPlanPriceQuote? Price,
    bool IsYearlyBilling,
    IReadOnlyList<string>? BusinessTypeCodes = null);

/// <summary>
/// The CentralDb plan price in effect for the registration's plan and period when the summary was read, the same price
/// a checkout would charge (WAS-147). Null when no price applies, and then no checkout can start.
/// </summary>
public sealed record BillingPlanPriceQuote(decimal AmountTry, bool VatIncluded);
