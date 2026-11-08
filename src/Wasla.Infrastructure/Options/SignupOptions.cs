namespace Wasla.Infrastructure.Options;

/// <summary>
/// Registration drafts and owner phone verification (WAS-192), bound from <c>Wasla:Signup</c>. The approved product values
/// are the defaults of <see cref="PhoneVerificationOptions.CodeLifetime"/>, <see cref="PhoneVerificationOptions.ResendWait"/>
/// and <see cref="PhoneVerificationOptions.MaxFailedAttempts"/> (the 6-digit code length is fixed in
/// <c>PhoneVerificationPolicy</c>); every other value is an operational default to tune, documented in
/// <c>docs/architecture/signup-registration.md</c>.
/// </summary>
public sealed class SignupOptions
{
    public const string SectionName = "Wasla:Signup";

    /// <summary>The support address the signup page shows. The mailbox's readiness is tracked outside this repository.</summary>
    public string SupportEmail { get; set; } = "destek@wasla.tr";

    public RegistrationDraftLifetimeOptions Draft { get; set; } = new();

    public PhoneVerificationOptions PhoneVerification { get; set; } = new();
}

/// <summary>How long a draft and its phone proof live, and how often expired rows are deleted (<c>Wasla:Signup:Draft</c>).</summary>
public sealed class RegistrationDraftLifetimeOptions
{
    /// <summary>A draft expires this long after its last successful save. Tunable.</summary>
    public TimeSpan IdleLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>A draft never lives longer than this after it was created, however often it is saved. Tunable.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromDays(3);

    /// <summary>A phone verification proof is accepted at submit for this long after verification. Tunable.</summary>
    public TimeSpan PhoneProofLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often expired drafts, their verification rows and old send records are deleted. Tunable.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>Code, attempt and send rules of owner phone verification (<c>Wasla:Signup:PhoneVerification</c>).</summary>
public sealed class PhoneVerificationOptions
{
    /// <summary>Approved: a code can be entered for 5 minutes.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Approved: a new code can be requested 60 seconds after the previous send.</summary>
    public TimeSpan ResendWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Approved: the 5th wrong code for a number starts the lock. Resends do not reset the count.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a number stays locked after <see cref="MaxFailedAttempts"/> wrong codes. Tunable.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Codes one draft may send over its whole life, across every number it used. Tunable.</summary>
    public int MaxSendsPerDraft { get; set; } = 6;

    /// <summary>Codes one number may receive in a rolling hour, across all drafts. Tunable.</summary>
    public int MaxSendsPerNumberPerHour { get; set; } = 5;

    /// <summary>Codes one number may receive in a rolling 24 hours, across all drafts. Tunable.</summary>
    public int MaxSendsPerNumberPerDay { get; set; } = 10;

    /// <summary>Global SMS volume (spend) guard: codes sent in a rolling hour, all numbers together. Tunable with SMS pricing (WAS-193).</summary>
    public int MaxSendsPerHour { get; set; } = 300;

    /// <summary>Global SMS volume (spend) guard: codes sent in a rolling 24 hours, all numbers together. Tunable with SMS pricing (WAS-193).</summary>
    public int MaxSendsPerDay { get; set; } = 2000;
}
