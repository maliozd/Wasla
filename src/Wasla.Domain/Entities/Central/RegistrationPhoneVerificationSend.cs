using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// One verification SMS handed to the sender (WAS-192), kept for the rolling per-number and global send limits. It has
/// no foreign key to its draft, so the counts survive the draft; cleanup deletes records older than the longest window.
/// </summary>
public class RegistrationPhoneVerificationSend
{
    public Guid Id { get; set; }

    public Guid DraftId { get; set; }

    /// <summary>E.164.</summary>
    public string PhoneNumber { get; set; } = default!;

    public DateTime SentAtUtc { get; set; }

    public RegistrationPhoneVerificationSendOutcome Outcome { get; set; }
}
