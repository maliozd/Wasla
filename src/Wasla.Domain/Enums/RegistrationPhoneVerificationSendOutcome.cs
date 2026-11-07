namespace Wasla.Domain.Enums;

/// <summary>What the SMS sender answered for one verification send (WAS-192).</summary>
public enum RegistrationPhoneVerificationSendOutcome
{
    /// <summary>Counted and handed to the sender; no answer recorded yet.</summary>
    Pending = 0,

    Accepted = 1,

    Failed = 2
}
