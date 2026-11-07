namespace Wasla.Web.Models.Signup;

// The JSON contract of the registration draft API (WAS-192, docs/architecture/signup-registration.md). MVC serializes
// with camelCase names. Enumerations are sent as the string constants below, never as numbers.

// --- Requests ---------------------------------------------------------------------------------------

/// <summary><c>PUT /signup/draft/plan</c>.</summary>
public sealed class SignupPlanSectionRequest
{
    public string? PlanCode { get; set; }

    /// <summary><c>Monthly</c> or <c>Yearly</c>.</summary>
    public string? BillingPeriod { get; set; }
}

/// <summary><c>PUT /signup/draft/restaurant</c>. The restaurant contact phone is not the owner's mobile phone.</summary>
public sealed class SignupRestaurantSectionRequest
{
    public string? BusinessName { get; set; }

    /// <summary>Business subtype codes, for example <c>burger</c>; at least one.</summary>
    public List<string>? BusinessTypeCodes { get; set; }

    /// <summary><c>Mobile</c> or <c>Landline</c>.</summary>
    public string? BusinessPhoneType { get; set; }

    public string? BusinessPhone { get; set; }

    public string? BusinessEmail { get; set; }

    /// <summary>The panel address label; the server normalizes it (trimmed, lower case).</summary>
    public string? Slug { get; set; }

    public string? Country { get; set; }

    public int? CityId { get; set; }

    public int? DistrictId { get; set; }

    public int? NeighborhoodId { get; set; }

    public int? StreetId { get; set; }

    /// <summary>Free text outside Türkiye; ignored for Türkiye, where the server resolves it from <see cref="CityId"/>.</summary>
    public string? City { get; set; }

    /// <summary>Free text outside Türkiye; ignored for Türkiye, where the server resolves it from <see cref="DistrictId"/>.</summary>
    public string? District { get; set; }

    public string? Neighborhood { get; set; }

    public string? StreetAddress { get; set; }

    public string? BuildingNumber { get; set; }

    public string? Floor { get; set; }

    public string? DoorNumber { get; set; }

    public string? AddressNote { get; set; }

    public string? PostalCode { get; set; }

    public string? LocationUrl { get; set; }
}

/// <summary>
/// <c>PUT /signup/draft/owner</c>. Send <see cref="Password"/> and <see cref="ConfirmPassword"/> only when the user typed
/// a password: empty on a later save keeps the stored hash. The password is never sent back.
/// </summary>
public sealed class SignupOwnerSectionRequest
{
    public string? FullName { get; set; }

    public string? Email { get; set; }

    /// <summary>The owner's mobile phone; Türkiye mobile numbers only (<c>532 123 45 67</c>, <c>0532…</c>, <c>+90 532…</c>).</summary>
    public string? MobilePhone { get; set; }

    public string? Password { get; set; }

    public string? ConfirmPassword { get; set; }
}

/// <summary><c>PUT /signup/draft/phone</c>: change the owner's mobile phone.</summary>
public sealed class SignupPhoneChangeRequest
{
    public string? MobilePhone { get; set; }
}

/// <summary><c>POST /signup/draft/phone/verify</c>.</summary>
public sealed class SignupPhoneVerifyRequest
{
    /// <summary>The code as entered; <see cref="SignupPhoneStatusResponse.CodeLength"/> digits.</summary>
    public string? Code { get; set; }
}

// --- Responses --------------------------------------------------------------------------------------

/// <summary>The section names used in field errors and <c>missingSections</c>.</summary>
public static class SignupDraftSections
{
    public const string Plan = "plan";
    public const string Restaurant = "restaurant";
    public const string Owner = "owner";
}

/// <summary>
/// <c>GET /signup/draft</c>, the answer of every successful section save, and the state the signup page embeds on render.
/// Never contains the password, its hash or a verification code.
/// </summary>
public sealed class SignupDraftStateResponse
{
    /// <summary>False when the browser has no live draft; every section is then incomplete and empty.</summary>
    public bool Exists { get; init; }

    /// <summary>Seconds until the draft expires (each successful save extends it); null without a draft.</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>True once the draft was submitted; <see cref="RegistrationUrl"/> then names its registration's status page.</summary>
    public bool Submitted { get; init; }

    public string? RegistrationUrl { get; init; }

    public SignupDraftPlanState Plan { get; init; } = new();

    public SignupDraftRestaurantState Restaurant { get; init; } = new();

    public SignupDraftOwnerState Owner { get; init; } = new();

    public SignupPhoneStatusResponse Phone { get; init; } = new();

    /// <summary>Every section saved and the owner's current mobile phone verified. A hint only: submit checks again.</summary>
    public bool CanReview { get; init; }

    /// <summary>The selected plan and period's price in effect; null until the plan section is saved.</summary>
    public SignupDraftPriceResponse? Price { get; init; }
}

public sealed class SignupDraftPlanState
{
    public bool Complete { get; init; }

    public string? PlanCode { get; init; }

    public string? BillingPeriod { get; init; }
}

/// <summary>The stored restaurant section, with the same field names as <see cref="SignupRestaurantSectionRequest"/>.</summary>
public sealed class SignupDraftRestaurantState
{
    public bool Complete { get; init; }

    public string? BusinessName { get; init; }

    public IReadOnlyList<string> BusinessTypeCodes { get; init; } = Array.Empty<string>();

    public string? BusinessPhoneType { get; init; }

    public string? BusinessPhone { get; init; }

    public string? BusinessEmail { get; init; }

    public string? Slug { get; init; }

    public string? Country { get; init; }

    public int? CityId { get; init; }

    public int? DistrictId { get; init; }

    public int? NeighborhoodId { get; init; }

    public int? StreetId { get; init; }

    public string? City { get; init; }

    public string? District { get; init; }

    public string? Neighborhood { get; init; }

    public string? StreetAddress { get; init; }

    public string? BuildingNumber { get; init; }

    public string? Floor { get; init; }

    public string? DoorNumber { get; init; }

    public string? AddressNote { get; init; }

    public string? PostalCode { get; init; }

    public string? LocationUrl { get; init; }
}

/// <summary>The stored owner section. Only <see cref="PasswordSet"/> says anything about the password.</summary>
public sealed class SignupDraftOwnerState
{
    public bool Complete { get; init; }

    public string? FullName { get; init; }

    public string? Email { get; init; }

    /// <summary>The national significant number, 10 digits without the +90 prefix, for example <c>5321234567</c>.</summary>
    public string? MobilePhone { get; init; }

    public bool PasswordSet { get; init; }
}

/// <summary>The values of <see cref="SignupPhoneStatusResponse.State"/>.</summary>
public static class SignupPhoneStates
{
    public const string NotSent = "notSent";
    public const string CodeSent = "codeSent";
    public const string Expired = "expired";
    public const string Verified = "verified";
    public const string Locked = "locked";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The owner phone verification status (<c>GET /signup/draft/phone</c>, every phone answer, and <c>phone</c> in the
/// draft state). Times are whole seconds from the server's clock, rounded up, so the browser's clock does not matter.
/// </summary>
public sealed class SignupPhoneStatusResponse
{
    /// <summary>One of <see cref="SignupPhoneStates"/>.</summary>
    public string State { get; init; } = SignupPhoneStates.NotSent;

    /// <summary>The owner's mobile phone with all but the first and the last two digits hidden, for example <c>+90 5** *** ** 67</c>; null until the owner section is saved.</summary>
    public string? MaskedPhone { get; init; }

    public int CodeLength { get; init; } = 6;

    /// <summary>Only while <c>codeSent</c>.</summary>
    public int? CodeExpiresInSeconds { get; init; }

    /// <summary>0 when a code can be sent now; the wait otherwise; null when sending is not possible (no number, verified, locked, unavailable).</summary>
    public int? ResendAvailableInSeconds { get; init; }

    /// <summary>Only while <c>locked</c>.</summary>
    public int? LockedForSeconds { get; init; }

    /// <summary>Only while <c>codeSent</c>: wrong codes left before the lock.</summary>
    public int? AttemptsRemaining { get; init; }
}

/// <summary>
/// The price checkout would charge now for the draft's plan and period (the authoritative Central Admin price reader).
/// No VAT amount, total with tax or renewal date is computed here.
/// </summary>
public sealed class SignupDraftPriceResponse
{
    public string PlanCode { get; init; } = string.Empty;

    public string BillingPeriod { get; init; } = string.Empty;

    /// <summary>False when no price is in effect for this plan and period; the amount fields are then null.</summary>
    public bool IsSet { get; init; }

    public decimal? Amount { get; init; }

    /// <summary>ISO 4217, for example <c>TRY</c>.</summary>
    public string? Currency { get; init; }

    public bool? VatIncluded { get; init; }

    /// <summary>Localized, for example <c>600 TL / ay</c>, or the localized "price not set yet" text.</summary>
    public string PriceText { get; init; } = string.Empty;

    /// <summary>Localized VAT note (<c>KDV dahil</c> / <c>KDV hariç</c>); null when no price is set.</summary>
    public string? VatText { get; init; }
}

/// <summary>One invalid field. <see cref="Code"/> is a stable SharedResource key, <see cref="Message"/> its localized text.</summary>
public sealed class SignupFieldError
{
    /// <summary>One of <see cref="SignupDraftSections"/>.</summary>
    public string Section { get; init; } = string.Empty;

    /// <summary>The camelCase request field name in that section, for example <c>slug</c> or <c>mobilePhone</c>.</summary>
    public string Field { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// The body of every JSON error answer of the draft API. <see cref="Code"/> is a stable SharedResource key
/// (<c>Signup.Draft.*</c>, <c>Signup.Phone.*</c> or an existing <c>Signup.*</c> key) and <see cref="Message"/> its text in
/// the request culture. Antiforgery failures (400) and closed signup (404) have no JSON body.
/// </summary>
public sealed class SignupApiError
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<SignupFieldError> FieldErrors { get; init; } = Array.Empty<SignupFieldError>();

    /// <summary>Set with 429 and 503 answers when waiting helps; the same value as the <c>Retry-After</c> header.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>The phone status after the request, on errors of the phone endpoints.</summary>
    public SignupPhoneStatusResponse? Phone { get; init; }

    /// <summary>With <c>Signup.Draft.Incomplete</c>: the sections never saved (<see cref="SignupDraftSections"/>).</summary>
    public IReadOnlyList<string>? MissingSections { get; init; }

    /// <summary>With <c>Signup.Draft.Submitted</c>: the status page of the registration the draft became.</summary>
    public string? RegistrationUrl { get; init; }
}

/// <summary><c>POST /signup/draft/submit</c> success: navigate to <see cref="RedirectUrl"/>.</summary>
public sealed class SignupSubmitResponse
{
    public Guid RegistrationId { get; init; }

    /// <summary>The existing status page, <c>/signup/pending/{id}</c>.</summary>
    public string RedirectUrl { get; init; } = string.Empty;
}

/// <summary>The values of <see cref="SignupAddressAvailabilityResponse.Status"/>.</summary>
public static class SignupAddressAvailabilityStatuses
{
    public const string Available = "available";
    public const string Taken = "taken";
    public const string Invalid = "invalid";
}

/// <summary><c>GET /signup/address-availability</c>: advisory only, never a reservation.</summary>
public sealed class SignupAddressAvailabilityResponse
{
    /// <summary>The normalized label (trimmed, lower case) the status is for.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>One of <see cref="SignupAddressAvailabilityStatuses"/>. Reserved names are <c>taken</c>.</summary>
    public string Status { get; init; } = SignupAddressAvailabilityStatuses.Invalid;
}

/// <summary>
/// <c>GET /signup/draft/phone/fake-outbox</c>, Development with the fake SMS sender only (404 elsewhere): the fake
/// messages sent to this draft's numbers, newest first.
/// </summary>
public sealed class SignupFakeSmsOutboxResponse
{
    public IReadOnlyList<SignupFakeSmsMessage> Messages { get; init; } = Array.Empty<SignupFakeSmsMessage>();
}

public sealed class SignupFakeSmsMessage
{
    public string MaskedPhone { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public DateTime SentAtUtc { get; init; }
}

/// <summary>The URLs the signup page's script calls; built on the server so no route is hard-coded in JavaScript.</summary>
public sealed record SignupDraftEndpoints(
    string Draft,
    string PlanSection,
    string RestaurantSection,
    string OwnerSection,
    string Phone,
    string PhoneSend,
    string PhoneVerify,
    string Submit,
    string AddressAvailability,
    string Districts,
    string Neighborhoods,
    string Streets,
    string? FakeSmsOutbox);
