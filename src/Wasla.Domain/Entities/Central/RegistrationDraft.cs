using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// A browser-bound, server-side registration draft (WAS-192): the signup form's sections saved step by step before any
/// payment-pending registration exists. Referenced only by the browser's protected draft cookie.
/// </summary>
/// <remarks>
/// Every personal field is nullable: a section is null until it is saved, and the submit clears them all in the same
/// transaction that creates the <see cref="PendingRegistration"/>. The row then stays until it expires so a repeated
/// submit can answer with the same registration. The password is stored only as a BCrypt hash.
/// </remarks>
public class RegistrationDraft
{
    public Guid Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The draft is treated as missing from this time on and is deleted by cleanup.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Raised by every write; an optimistic concurrency token.</summary>
    public int Revision { get; set; }

    // --- Plan -------------------------------------------------------------------------------------

    public string? PlanCode { get; set; }

    public string? BillingPeriod { get; set; }

    public DateTime? PlanSavedAtUtc { get; set; }

    // --- Restaurant -------------------------------------------------------------------------------

    public string? BusinessName { get; set; }

    /// <summary>Business subtype codes joined with commas.</summary>
    public string? BusinessTypeCodes { get; set; }

    public BusinessPhoneType? BusinessPhoneType { get; set; }

    public string? BusinessPhone { get; set; }

    public string? BusinessEmail { get; set; }

    public string? Slug { get; set; }

    public string? Country { get; set; }

    public int? CityId { get; set; }

    public int? DistrictId { get; set; }

    public int? NeighborhoodId { get; set; }

    public int? StreetId { get; set; }

    public string? City { get; set; }

    public string? District { get; set; }

    public string? Neighborhood { get; set; }

    public string? StreetAddress { get; set; }

    public string? BuildingNumber { get; set; }

    public string? Floor { get; set; }

    public string? DoorNumber { get; set; }

    public string? AddressNote { get; set; }

    public string? PostalCode { get; set; }

    public string? LocationUrl { get; set; }

    public DateTime? RestaurantSavedAtUtc { get; set; }

    // --- Account owner ----------------------------------------------------------------------------

    public string? OwnerFullName { get; set; }

    public string? OwnerEmail { get; set; }

    /// <summary>The owner's mobile phone in E.164 (<c>+905321234567</c>).</summary>
    public string? OwnerMobilePhone { get; set; }

    /// <summary>BCrypt hash. Never log or return this value.</summary>
    public string? PasswordHash { get; set; }

    public DateTime? OwnerSavedAtUtc { get; set; }

    // --- Phone proof ------------------------------------------------------------------------------

    /// <summary>The E.164 number that was verified. The proof counts only while it equals <see cref="OwnerMobilePhone"/>.</summary>
    public string? PhoneVerifiedNumber { get; set; }

    public DateTime? PhoneVerifiedAtUtc { get; set; }

    // --- Submission -------------------------------------------------------------------------------

    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>The registration this draft became. A draft yields at most one.</summary>
    public Guid? PendingRegistrationId { get; set; }
}
