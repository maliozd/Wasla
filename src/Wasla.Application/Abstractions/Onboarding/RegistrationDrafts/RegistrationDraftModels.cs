namespace Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;

/// <summary>The form sections a registration draft stores, in the order of the signup flow (WAS-192).</summary>
public enum RegistrationDraftSection
{
    Plan = 1,
    Restaurant = 2,
    Owner = 3
}

/// <summary>The plan section as the browser submitted it. Validated and normalized by the draft service.</summary>
public sealed record RegistrationPlanInput(string? PlanCode, string? BillingPeriod);

/// <summary>
/// The restaurant section as the browser submitted it. The restaurant contact phone is separate from the owner's mobile
/// phone and keeps its Mobile/Landline type. Names of a Türkiye city, district, neighborhood and street are resolved on
/// the server from their ids; free-text city and district apply to other countries only.
/// </summary>
public sealed record RegistrationRestaurantInput(
    string? BusinessName,
    IReadOnlyList<string>? BusinessTypeCodes,
    string? BusinessPhoneType,
    string? BusinessPhone,
    string? BusinessEmail,
    string? Slug,
    string? Country,
    int? CityId,
    int? DistrictId,
    int? NeighborhoodId,
    int? StreetId,
    string? City,
    string? District,
    string? Neighborhood,
    string? StreetAddress,
    string? BuildingNumber,
    string? Floor,
    string? DoorNumber,
    string? AddressNote,
    string? PostalCode,
    string? LocationUrl);

/// <summary>
/// The account owner section as the browser submitted it. The password lives only in this request: the draft service
/// hashes it with the established BCrypt boundary before anything is stored, and an empty password on a later save
/// keeps the stored hash. Never log this object; <see cref="ToString"/> leaves the password out.
/// </summary>
public sealed class RegistrationOwnerInput
{
    public string? FullName { get; init; }

    public string? Email { get; init; }

    /// <summary>The owner's mobile phone as typed; the server normalizes it to E.164 (Türkiye mobile numbers only).</summary>
    public string? MobilePhone { get; init; }

    public string? Password { get; init; }

    public string? ConfirmPassword { get; init; }

    public override string ToString() => nameof(RegistrationOwnerInput);
}

/// <summary>One invalid field of a section. <see cref="Code"/> is a stable SharedResource key.</summary>
/// <param name="Section">The section the field belongs to.</param>
/// <param name="Field">The camelCase name of the field in that section's JSON request (for example <c>businessName</c>).</param>
/// <param name="Code">A SharedResource key such as <c>Validation.SlugInvalid</c>.</param>
public sealed record RegistrationDraftFieldError(RegistrationDraftSection Section, string Field, string Code);

public sealed record RegistrationDraftPlan(string PlanCode, string BillingPeriod);

/// <summary>The stored restaurant section: trimmed, normalized and with resolved address names.</summary>
public sealed record RegistrationDraftRestaurant(
    string BusinessName,
    IReadOnlyList<string> BusinessTypeCodes,
    string BusinessPhoneType,
    string BusinessPhone,
    string? BusinessEmail,
    string Slug,
    string Country,
    int? CityId,
    int? DistrictId,
    int? NeighborhoodId,
    int? StreetId,
    string? City,
    string? District,
    string? Neighborhood,
    string? StreetAddress,
    string? BuildingNumber,
    string? Floor,
    string? DoorNumber,
    string? AddressNote,
    string? PostalCode,
    string? LocationUrl);

/// <summary>
/// The stored owner section. It never carries the password or its hash: <see cref="PasswordSet"/> only says that a hash
/// is stored.
/// </summary>
public sealed record RegistrationDraftOwner(string FullName, string Email, string MobilePhoneE164, bool PasswordSet);

/// <summary>
/// A registration draft as its own browser may see it. A section is null until it has been saved once; a saved section
/// passed its server-side validation when it was saved.
/// </summary>
public sealed record RegistrationDraftView(
    Guid Id,
    DateTime ExpiresAtUtc,
    RegistrationDraftPlan? Plan,
    RegistrationDraftRestaurant? Restaurant,
    RegistrationDraftOwner? Owner,
    PhoneVerificationStatus Phone,
    Guid? SubmittedRegistrationId)
{
    public bool IsSubmitted => SubmittedRegistrationId is not null;

    /// <summary>
    /// Whether the review step may be shown: every section saved and the owner's current mobile phone verified. Only a
    /// hint for the page; the submit re-validates everything on the server.
    /// </summary>
    public bool CanReview =>
        !IsSubmitted
        && Plan is not null
        && Restaurant is not null
        && Owner is not null
        && Phone.State == PhoneVerificationState.Verified;
}

/// <summary>
/// The outcome of saving one section. On success <see cref="Draft"/> is the saved draft; <see cref="Created"/> is true
/// when this save created it, so the caller issues the draft cookie. On failure <see cref="ErrorCode"/> is one of
/// <see cref="SignupErrorCodes"/> and nothing was written.
/// </summary>
public sealed record RegistrationDraftSaveResult(
    RegistrationDraftView? Draft,
    bool Created,
    string? ErrorCode,
    IReadOnlyList<RegistrationDraftFieldError> FieldErrors)
{
    public bool Succeeded => ErrorCode is null;

    public static RegistrationDraftSaveResult Saved(RegistrationDraftView draft, bool created) =>
        new(draft, created, null, Array.Empty<RegistrationDraftFieldError>());

    public static RegistrationDraftSaveResult Failed(string errorCode) =>
        new(null, false, errorCode, Array.Empty<RegistrationDraftFieldError>());

    public static RegistrationDraftSaveResult Invalid(IReadOnlyList<RegistrationDraftFieldError> fieldErrors) =>
        new(null, false, SignupErrorCodes.DraftValidationFailed, fieldErrors);
}

/// <summary>
/// The outcome of submitting a draft (“Proceed to secure payment”). On success <see cref="RegistrationId"/> names the
/// payment-pending registration; <see cref="AlreadySubmitted"/> is true when an earlier submit of the same draft created
/// it (a double click or a retried request), which is answered with the same registration.
/// </summary>
public sealed record RegistrationDraftSubmitResult(
    Guid? RegistrationId,
    bool AlreadySubmitted,
    string? ErrorCode,
    IReadOnlyList<RegistrationDraftFieldError> FieldErrors,
    IReadOnlyList<RegistrationDraftSection> MissingSections)
{
    public bool Succeeded => ErrorCode is null && RegistrationId is not null;

    public static RegistrationDraftSubmitResult Submitted(Guid registrationId, bool alreadySubmitted) =>
        new(registrationId, alreadySubmitted, null, Array.Empty<RegistrationDraftFieldError>(), Array.Empty<RegistrationDraftSection>());

    public static RegistrationDraftSubmitResult Failed(string errorCode) =>
        new(null, false, errorCode, Array.Empty<RegistrationDraftFieldError>(), Array.Empty<RegistrationDraftSection>());
}
