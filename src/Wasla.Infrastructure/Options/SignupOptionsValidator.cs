using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Options;

/// <summary>
/// Refuses <c>Wasla:Signup</c> values outside their bounds (WAS-192), so a typing error cannot disable a limit or make a
/// code live for hours. The bounds are documented in <c>docs/architecture/signup-registration.md</c>.
/// </summary>
public sealed class SignupOptionsValidator : IValidateOptions<SignupOptions>
{
    public const int MaxSendLimit = 100_000;

    public ValidateOptionsResult Validate(string? name, SignupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        const string section = SignupOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.SupportEmail) || !options.SupportEmail.Contains('@'))
            failures.Add($"{section}:SupportEmail must be an e-mail address.");

        var draft = options.Draft;
        if (draft is null)
        {
            failures.Add($"{section}:Draft is required.");
        }
        else
        {
            Range(failures, $"{section}:Draft:IdleLifetime", draft.IdleLifetime, TimeSpan.FromHours(1), TimeSpan.FromDays(7));
            Range(failures, $"{section}:Draft:MaxLifetime", draft.MaxLifetime, draft.IdleLifetime, TimeSpan.FromDays(30));
            Range(failures, $"{section}:Draft:PhoneProofLifetime", draft.PhoneProofLifetime, TimeSpan.FromMinutes(10), draft.MaxLifetime);
            Range(failures, $"{section}:Draft:CleanupInterval", draft.CleanupInterval, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        }

        var phone = options.PhoneVerification;
        if (phone is null)
        {
            failures.Add($"{section}:PhoneVerification is required.");
        }
        else
        {
            var prefix = $"{section}:PhoneVerification";
            Range(failures, $"{prefix}:CodeLifetime", phone.CodeLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
            Range(failures, $"{prefix}:ResendWait", phone.ResendWait, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
            if (phone.ResendWait >= phone.CodeLifetime)
                failures.Add($"{prefix}:ResendWait must be shorter than CodeLifetime.");
            Count(failures, $"{prefix}:MaxFailedAttempts", phone.MaxFailedAttempts, 1, 20);
            Range(failures, $"{prefix}:LockoutDuration", phone.LockoutDuration, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
            Count(failures, $"{prefix}:MaxSendsPerDraft", phone.MaxSendsPerDraft, 1, MaxSendLimit);
            Count(failures, $"{prefix}:MaxSendsPerNumberPerHour", phone.MaxSendsPerNumberPerHour, 1, MaxSendLimit);
            Count(failures, $"{prefix}:MaxSendsPerNumberPerDay", phone.MaxSendsPerNumberPerDay, 1, MaxSendLimit);
            Count(failures, $"{prefix}:MaxSendsPerHour", phone.MaxSendsPerHour, 1, MaxSendLimit);
            Count(failures, $"{prefix}:MaxSendsPerDay", phone.MaxSendsPerDay, 1, MaxSendLimit);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Range(List<string> failures, string key, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
            failures.Add($"{key} must be between {min:c} and {max:c}.");
    }

    private static void Count(List<string> failures, string key, int value, int min, int max)
    {
        if (value < min || value > max)
            failures.Add($"{key} must be between {min} and {max}.");
    }
}
