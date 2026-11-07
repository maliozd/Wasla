namespace Wasla.Application.Abstractions.Sms;

/// <summary>
/// Sends one SMS (WAS-192). Configured by <c>Wasla:Sms:Mode</c>: <c>Disabled</c> (also when missing) registers a sender
/// that is not available, so phone verification fails closed; <c>Fake</c> (Development only, refused elsewhere at startup)
/// keeps messages in an in-memory outbox. The real provider adapter is WAS-193; it keeps vendor types and credentials on
/// its side of this interface.
/// </summary>
/// <remarks>
/// <para>A message carries a one-time code. Implementations never log its text or the full number, and never place either
/// in an exception message.</para>
/// <para>Adapters report a refusal, a provider failure and a timeout as a <see cref="SmsDeliveryResult"/> and do not throw
/// for them; only the caller's own cancellation ends a send with an exception. If an adapter throws anyway, the send counts
/// as failed (a cancellation that is not the caller's as <see cref="SmsDeliveryFailure.Timeout"/>) and its code is
/// dropped.</para>
/// </remarks>
public interface ISmsSender
{
    /// <summary>False when no real or Development sender is configured; callers then refuse before generating a code.</summary>
    bool IsAvailable { get; }

    /// <summary>Hands one message to the provider. Returns the provider's answer; does not retry on its own.</summary>
    Task<SmsDeliveryResult> SendAsync(SmsMessage message, CancellationToken ct);
}

/// <summary>Why a message is sent; lets an adapter choose a sender identity or template per purpose.</summary>
public enum SmsPurpose
{
    RegistrationPhoneVerification = 1
}

/// <summary>One SMS. Never log it: the text contains a one-time code.</summary>
/// <param name="ToE164">The recipient in E.164, for example <c>+905321234567</c>.</param>
/// <param name="Text">The full message text, already localized.</param>
/// <param name="Purpose">Why it is sent.</param>
/// <param name="Reference">
/// An opaque Wasla reference of what the message is for (the registration draft id), which an adapter may pass as the
/// provider's client reference. Never personal data.
/// </param>
public sealed record SmsMessage(string ToE164, string Text, SmsPurpose Purpose, string? Reference = null)
{
    public override string ToString() => $"{nameof(SmsMessage)} {{ Purpose = {Purpose} }}";
}

/// <summary>Why a message was not accepted. Categories only; never provider payloads.</summary>
public enum SmsDeliveryFailure
{
    None = 0,

    /// <summary>The provider refused the message or the number.</summary>
    Rejected = 1,

    /// <summary>The provider could not be reached or answered with a server error.</summary>
    Unavailable = 2,

    /// <summary>No answer in time; whether the message was sent is unknown.</summary>
    Timeout = 3
}

public sealed record SmsDeliveryResult(bool Accepted, SmsDeliveryFailure Failure)
{
    public static SmsDeliveryResult Sent { get; } = new(true, SmsDeliveryFailure.None);

    public static SmsDeliveryResult Failed(SmsDeliveryFailure failure) => new(false, failure);
}
