namespace OrderHub.Application.Abstractions.Onboarding.PendingRegistrations;

public sealed record PendingRegistrationRequest(
    string PlanCode,
    string BillingPeriod,
    string BusinessName,
    IReadOnlyList<string> BusinessTypeCodes,
    string BusinessPhoneType,
    string BusinessPhone,
    string Slug,
    string Country,
    int? CityId,
    int? DistrictId,
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
