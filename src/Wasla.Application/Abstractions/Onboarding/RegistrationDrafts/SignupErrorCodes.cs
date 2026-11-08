namespace Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;

/// <summary>
/// Stable error codes of the registration draft and phone verification operations (WAS-192). Each code is a
/// SharedResource key: Web returns it as <c>code</c> and its localized text as <c>message</c>. Field errors use the
/// validators' own <c>Validation.*</c> keys. Codes are a contract with the signup page; do not rename them.
/// </summary>
public static class SignupErrorCodes
{
    // --- Draft --------------------------------------------------------------------------------------

    /// <summary>The request has no draft, or its draft cookie names a draft that is unknown, foreign or expired.</summary>
    public const string DraftMissing = "Signup.Draft.Missing";

    /// <summary>The draft was already submitted; it can no longer be changed.</summary>
    public const string DraftSubmitted = "Signup.Draft.Submitted";

    /// <summary>One or more fields are invalid; the field errors say which.</summary>
    public const string DraftValidationFailed = "Signup.Draft.ValidationFailed";

    /// <summary>The submit found a section that was never saved.</summary>
    public const string DraftIncomplete = "Signup.Draft.Incomplete";

    /// <summary>The request body is missing or is not the expected JSON.</summary>
    public const string DraftInvalidRequest = "Signup.Draft.InvalidRequest";

    // --- Phone verification -------------------------------------------------------------------------

    /// <summary>The owner section (with the mobile phone) must be saved before a code is sent or the number changed.</summary>
    public const string PhoneOwnerIncomplete = "Signup.Phone.OwnerIncomplete";

    /// <summary>The current number is already verified; nothing was sent or consumed.</summary>
    public const string PhoneAlreadyVerified = "Signup.Phone.AlreadyVerified";

    /// <summary>The submit found no valid proof for the owner's current mobile phone.</summary>
    public const string PhoneNotVerified = "Signup.Phone.NotVerified";

    /// <summary>The code is wrong. Counts as a failed attempt.</summary>
    public const string PhoneCodeIncorrect = "Signup.Phone.CodeIncorrect";

    /// <summary>The code for the current number expired.</summary>
    public const string PhoneCodeExpired = "Signup.Phone.CodeExpired";

    /// <summary>The entered value is not <see cref="PhoneVerificationPolicy.CodeLength"/> digits. Not counted as an attempt.</summary>
    public const string PhoneCodeFormatInvalid = "Signup.Phone.CodeFormatInvalid";

    /// <summary>No code is active for the current number (never sent, replaced by a number change, or already used).</summary>
    public const string PhoneNoActiveCode = "Signup.Phone.NoActiveCode";

    /// <summary>The resend wait since the last code has not passed.</summary>
    public const string PhoneResendNotAvailable = "Signup.Phone.ResendNotAvailable";

    /// <summary>Too many wrong codes for the current number; wait until the lock ends.</summary>
    public const string PhoneLocked = "Signup.Phone.Locked";

    /// <summary>The draft or the number has used up its sends for now.</summary>
    public const string PhoneSendLimitReached = "Signup.Phone.SendLimitReached";

    /// <summary>The SMS sender refused or failed the message; the code it carried is not valid.</summary>
    public const string PhoneDeliveryFailed = "Signup.Phone.DeliveryFailed";

    /// <summary>No SMS sender is configured in this environment (fail closed).</summary>
    public const string PhoneUnavailable = "Signup.Phone.Unavailable";

    /// <summary>The global SMS volume limit is reached; try again later.</summary>
    public const string PhoneCapacityReached = "Signup.Phone.CapacityReached";

    // --- Existing signup codes reused by the draft API ------------------------------------------------

    /// <summary>A network rate limit, or the per-owner-email cap of active registrations (the answer does not say which).</summary>
    public const string TooManyAttempts = "Signup.TooManyAttempts";

    /// <summary>The panel address is taken or reserved (also the final conflict at submit).</summary>
    public const string SlugUnavailable = "Signup.SlugUnavailable";

    /// <summary>The selected plan is sold through sales only (Enterprise).</summary>
    public const string EnterpriseContactOnly = "Signup.EnterpriseContactOnly";

    /// <summary>The registration could not be stored.</summary>
    public const string RegistrationFailed = "Signup.ProvisioningFailed";
}
