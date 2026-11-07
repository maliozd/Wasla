namespace Wasla.Application.Abstractions.Onboarding.RegistrationDrafts;

/// <summary>
/// Mandatory SMS verification of a registration draft owner's mobile phone (WAS-192). Every operation is tied to one
/// draft (from its protected cookie) and to the owner's mobile phone stored in that draft, normalized to E.164. A client
/// never states that a number is verified: the proof exists only in CentralDb and is checked again at submit.
/// </summary>
/// <remarks>
/// Codes are cryptographically random, stored only in protected form, single use, replaced by a resend, invalidated by
/// a number change, and never returned, logged or stored in plaintext. Failed attempts of a number accumulate across
/// resends until the lock they cause has passed.
/// </remarks>
public interface IRegistrationPhoneVerificationService
{
    /// <summary>Whether this environment has an SMS sender (false: phone verification is unavailable and fails closed).</summary>
    bool IsAvailable { get; }

    /// <summary>The status of the draft's current owner mobile phone, or <see cref="SignupErrorCodes.DraftMissing"/>.</summary>
    Task<PhoneVerificationResult> GetStatusAsync(Guid draftId, CancellationToken ct);

    /// <summary>
    /// Sends a new code to the draft's current owner mobile phone (first send or resend). A new code replaces the previous
    /// one and does not reset accumulated failures. Refused while the resend wait, a lock or a draft, number or global
    /// send limit applies. <paramref name="messageFormat"/> is the localized SMS text, a composite format with
    /// <c>{0}</c> for the code and <c>{1}</c> for its lifetime in minutes.
    /// </summary>
    Task<PhoneVerificationResult> SendCodeAsync(Guid draftId, string messageFormat, CancellationToken ct);

    /// <summary>
    /// Checks a code against the active code of the draft's current number and, when it matches, consumes it atomically
    /// and stores the verification proof (number and time) on the draft. Every checked code counts as an attempt before it
    /// is compared, so concurrent guesses cannot exceed the attempt limit.
    /// </summary>
    Task<PhoneVerificationResult> VerifyCodeAsync(Guid draftId, string? code, CancellationToken ct);

    /// <summary>
    /// Changes the owner's mobile phone of a draft whose owner section is saved. A different number clears the proof and
    /// invalidates every outstanding code of the draft; the same number changes nothing. Sends nothing.
    /// </summary>
    Task<PhoneVerificationResult> ChangeNumberAsync(Guid draftId, string? mobilePhone, CancellationToken ct);
}
