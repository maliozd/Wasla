namespace Wasla.Domain.Entities.Central;

/// <summary>
/// The SMS verification state of one mobile number of one registration draft (WAS-192): its active code, attempts,
/// sends, lock and verification time. Separate from <see cref="PendingRegistration"/>; deleted with its draft.
/// </summary>
/// <remarks>
/// The code is never stored in plaintext: <see cref="ProtectedCode"/> is a Data Protection payload that binds the code to
/// this row. Failed attempts accumulate across resends until the lock they cause has passed.
/// </remarks>
public class RegistrationPhoneVerification
{
    public Guid Id { get; set; }

    public Guid DraftId { get; set; }

    /// <summary>E.164.</summary>
    public string PhoneNumber { get; set; } = default!;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The active code in protected form; null when no code can be entered.</summary>
    public string? ProtectedCode { get; set; }

    public DateTime? CodeIssuedAtUtc { get; set; }

    public DateTime? CodeExpiresAtUtc { get; set; }

    /// <summary>The time of the last send; also the concurrency token of sends.</summary>
    public DateTime? LastSentAtUtc { get; set; }

    public int SendCount { get; set; }

    /// <summary>Checked codes that did not verify (a check in progress counts until it succeeds).</summary>
    public int FailedAttempts { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    public DateTime? VerifiedAtUtc { get; set; }
}
