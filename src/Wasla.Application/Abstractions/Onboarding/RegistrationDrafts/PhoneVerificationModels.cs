namespace Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;

/// <summary>The fixed parts of owner phone verification that the page and the server share (WAS-192).</summary>
public static class PhoneVerificationPolicy
{
    /// <summary>Approved: a verification code is 6 numeric digits.</summary>
    public const int CodeLength = 6;
}

/// <summary>Where the verification of the draft owner's current mobile phone stands.</summary>
public enum PhoneVerificationState
{
    /// <summary>No code is active for the current number (none sent yet, or the owner section is not saved).</summary>
    NotSent = 0,

    /// <summary>A code was sent to the current number and can still be entered.</summary>
    CodeSent = 1,

    /// <summary>The last code for the current number expired unused; a new one must be requested.</summary>
    Expired = 2,

    /// <summary>The current number holds a valid verification proof.</summary>
    Verified = 3,

    /// <summary>Too many wrong codes for the current number; nothing can be verified or sent until the wait ends.</summary>
    Locked = 4,

    /// <summary>No SMS sender is configured in this environment, so no code can be sent (fail closed).</summary>
    Unavailable = 5
}

/// <summary>
/// The verification state of a draft's current owner mobile phone. Durations are relative to the server's clock when
/// the status was read, so the browser's clock never matters. Never carries a code.
/// </summary>
/// <param name="State">The current state.</param>
/// <param name="PhoneE164">The owner's mobile phone in E.164 (<c>+905321234567</c>); null until the owner section is saved. Web masks it.</param>
/// <param name="CodeLength">Always <see cref="PhoneVerificationPolicy.CodeLength"/>.</param>
/// <param name="CodeExpiresIn">While <see cref="PhoneVerificationState.CodeSent"/>: the time left to enter the active code.</param>
/// <param name="ResendAvailableIn">Zero when a code may be sent now; the remaining wait otherwise; null when sending is not possible (no number, verified, locked, unavailable).</param>
/// <param name="LockedFor">While <see cref="PhoneVerificationState.Locked"/>: the remaining wait.</param>
/// <param name="AttemptsRemaining">While <see cref="PhoneVerificationState.CodeSent"/>: wrong codes left before the lock.</param>
public sealed record PhoneVerificationStatus(
    PhoneVerificationState State,
    string? PhoneE164,
    int CodeLength,
    TimeSpan? CodeExpiresIn,
    TimeSpan? ResendAvailableIn,
    TimeSpan? LockedFor,
    int? AttemptsRemaining);

/// <summary>
/// The outcome of a phone verification operation. <see cref="Status"/> is the state after the operation (also on most
/// failures, so the page can refresh its timers); <see cref="ErrorCode"/> is one of <see cref="SignupErrorCodes"/>;
/// <see cref="RetryAfter"/> is set when waiting helps (resend wait, lock, send limits, capacity).
/// </summary>
public sealed record PhoneVerificationResult(
    PhoneVerificationStatus? Status,
    string? ErrorCode,
    TimeSpan? RetryAfter,
    IReadOnlyList<RegistrationDraftFieldError> FieldErrors)
{
    public bool Succeeded => ErrorCode is null;

    public static PhoneVerificationResult Ok(PhoneVerificationStatus status) =>
        new(status, null, null, Array.Empty<RegistrationDraftFieldError>());

    public static PhoneVerificationResult Failed(string errorCode, PhoneVerificationStatus? status = null, TimeSpan? retryAfter = null) =>
        new(status, errorCode, retryAfter, Array.Empty<RegistrationDraftFieldError>());
}
