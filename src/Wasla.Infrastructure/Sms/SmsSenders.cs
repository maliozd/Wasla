using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Wasla.Application.Abstractions.Sms;

namespace Wasla.Infrastructure.Sms;

/// <summary>
/// The sender when <c>Wasla:Sms:Mode</c> is missing, empty or <c>Disabled</c>: not available, so owner phone verification
/// fails closed and no signup can pass the submit boundary (WAS-192).
/// </summary>
public sealed class DisabledSmsSender : ISmsSender
{
    public bool IsAvailable => false;

    public Task<SmsDeliveryResult> SendAsync(SmsMessage message, CancellationToken ct) =>
        Task.FromResult(SmsDeliveryResult.Failed(SmsDeliveryFailure.Unavailable));
}

/// <summary>
/// The Development and test sender (<c>Wasla:Sms:Mode</c> = <c>Fake</c>, refused at startup outside Development; test
/// hosts register it themselves). It sends nothing and logs nothing: messages go to the <see cref="FakeSmsOutbox"/>.
/// </summary>
public sealed class FakeSmsSender : ISmsSender
{
    private readonly FakeSmsOutbox _outbox;
    private readonly TimeProvider _time;

    public FakeSmsSender(FakeSmsOutbox outbox, TimeProvider time)
    {
        _outbox = outbox;
        _time = time;
    }

    public bool IsAvailable => true;

    public Task<SmsDeliveryResult> SendAsync(SmsMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_outbox.ThrowOnSend is { } exception)
            throw exception;
        if (_outbox.FailDeliveriesWith is { } failure && failure != SmsDeliveryFailure.None)
            return Task.FromResult(SmsDeliveryResult.Failed(failure));

        _outbox.Add(message, _time.GetUtcNow().UtcDateTime);
        return Task.FromResult(SmsDeliveryResult.Sent);
    }
}

/// <summary>
/// The in-memory outbox of <see cref="FakeSmsSender"/>: the newest 100 messages of the process. Read by the
/// Development-only fake outbox endpoint and, through DI, by tests. Never used by a real sender.
/// </summary>
public sealed partial class FakeSmsOutbox
{
    public const int Capacity = 100;

    private readonly ConcurrentQueue<FakeSmsMessage> _messages = new();

    /// <summary>For tests: while set, every send fails with this category and nothing is stored.</summary>
    public SmsDeliveryFailure? FailDeliveriesWith { get; set; }

    /// <summary>For tests: while set, every send throws it, as a misbehaving adapter would (for example a timeout).</summary>
    public Exception? ThrowOnSend { get; set; }

    public void Add(SmsMessage message, DateTime sentAtUtc)
    {
        var match = OneTimeCode().Match(message.Text);
        _messages.Enqueue(new FakeSmsMessage(message.ToE164, message.Text, match.Success ? match.Value : string.Empty, message.Purpose, sentAtUtc));
        while (_messages.Count > Capacity && _messages.TryDequeue(out _))
        {
        }
    }

    /// <summary>The messages sent to <paramref name="toE164"/>, newest first.</summary>
    public IReadOnlyList<FakeSmsMessage> MessagesTo(string toE164) =>
        _messages.Where(m => string.Equals(m.ToE164, toE164, StringComparison.Ordinal))
            .Reverse()
            .ToArray();

    /// <summary>The code of the newest message to <paramref name="toE164"/>, or null.</summary>
    public string? LatestCodeTo(string toE164) => MessagesTo(toE164).FirstOrDefault()?.Code;

    public IReadOnlyList<FakeSmsMessage> All => _messages.Reverse().ToArray();

    public void Clear() => _messages.Clear();

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex OneTimeCode();
}

/// <param name="Code">The 6-digit code found in the text.</param>
public sealed record FakeSmsMessage(string ToE164, string Text, string Code, SmsPurpose Purpose, DateTime SentAtUtc)
{
    public override string ToString() => nameof(FakeSmsMessage);
}
