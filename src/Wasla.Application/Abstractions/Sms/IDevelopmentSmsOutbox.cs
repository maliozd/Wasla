namespace Wasla.Application.Abstractions.Sms;

/// <summary>
/// The fake sender's messages, for a developer reading a code locally (WAS-192). Registered only in Development with
/// <c>Wasla:Sms:Mode</c> = <c>Fake</c>; absent everywhere else, where nothing can read a code.
/// </summary>
public interface IDevelopmentSmsOutbox
{
    /// <summary>The messages sent with <paramref name="reference"/> (a registration draft id), newest first.</summary>
    IReadOnlyList<DevelopmentSmsMessage> MessagesFor(string reference);
}

/// <param name="ToE164">The recipient.</param>
/// <param name="Code">The one-time code found in the text.</param>
/// <param name="SentAtUtc">When the fake sender stored it.</param>
public sealed record DevelopmentSmsMessage(string ToE164, string Code, DateTime SentAtUtc)
{
    public override string ToString() => nameof(DevelopmentSmsMessage);
}
