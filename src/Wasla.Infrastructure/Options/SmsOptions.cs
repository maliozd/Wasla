namespace Wasla.Infrastructure.Options;

/// <summary>
/// Which SMS sender the host uses (WAS-192), bound from <c>Wasla:Sms</c>. Missing, empty or <c>Disabled</c>: no sender,
/// so owner phone verification is unavailable and signup cannot pass the review/payment boundary (fail closed).
/// <c>Fake</c>: the in-memory Development outbox; the host refuses to start with it outside Development. Any other value
/// stops startup. The real provider mode is added by WAS-193.
/// </summary>
public sealed class SmsOptions
{
    public const string SectionName = "Wasla:Sms";

    public string? Mode { get; set; }
}

/// <summary>The accepted values of <see cref="SmsOptions.Mode"/>.</summary>
public static class SmsModes
{
    public const string Disabled = "Disabled";

    public const string Fake = "Fake";
}
