using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Security.RateLimiting;

/// <summary>
/// Names of the credential-entry and anonymous onboarding rate-limit policies (Wasla.Web logins and signup, Wasla.Api
/// credential check).
/// </summary>
public static class AuthenticationRateLimitPolicies
{
    /// <summary>Tenant Web login, <c>POST /auth/login</c>. Partitioned by resolved tenant and client network.</summary>
    public const string TenantLogin = "auth-tenant-login";

    /// <summary>Central Admin login, <c>POST /admin/login</c>. Partitioned by client network.</summary>
    public const string CentralAdminLogin = "auth-central-admin-login";

    /// <summary>Api credential check, <c>POST /api/auth/validate</c>. Partitioned by resolved tenant and client network.</summary>
    public const string ApiCredentialValidation = "auth-api-credential-validation";

    /// <summary>Anonymous signup, <c>POST /signup</c>, which creates a pending registration. Partitioned by client network.</summary>
    public const string SignupSubmission = "signup-submission";

    /// <summary>
    /// The checkout POSTs that change a pending registration (<c>/checkout/cancel</c> and the Development payment
    /// simulator). Partitioned by client network.
    /// </summary>
    public const string SignupCheckout = "signup-checkout";

    /// <summary>
    /// The anonymous public contact form, <c>POST /contact</c>, which stores a message and sends an email. Partitioned
    /// by client network.
    /// </summary>
    public const string ContactSubmission = "contact-submission";

    /// <summary>
    /// Registration draft writes (WAS-192): the section saves under <c>/signup/draft</c> and the owner phone change.
    /// Partitioned by client network.
    /// </summary>
    public const string SignupDraftSave = "signup-draft-save";

    /// <summary>
    /// A draft write that creates a new registration draft (the request has no live draft cookie), counted on top of
    /// <see cref="SignupDraftSave"/>. Partitioned by client network.
    /// </summary>
    public const string SignupDraftCreation = "signup-draft-creation";

    /// <summary>Owner phone verification code sends and resends, <c>POST /signup/draft/phone/send</c>. Partitioned by client network.</summary>
    public const string SignupPhoneSend = "signup-phone-send";

    /// <summary>Owner phone verification code checks, <c>POST /signup/draft/phone/verify</c>. Partitioned by client network.</summary>
    public const string SignupPhoneVerify = "signup-phone-verify";

    /// <summary>The advisory panel-address check, <c>GET /signup/address-availability</c>. Partitioned by client network.</summary>
    public const string SignupAddressAvailability = "signup-address-availability";

    /// <summary>Whether the policy's partitions are per tenant (the tenant resolved from the host, never from the request body).</summary>
    public static bool IsTenantScoped(string policy) =>
        policy is TenantLogin or ApiCredentialValidation;
}

/// <summary>
/// Limits for the credential-entry and signup endpoints, bound from <c>AuthenticationRateLimiting</c>. Each policy is a fixed window:
/// at most <see cref="AuthenticationRateLimitRule.PermitLimit"/> attempts per partition per
/// <see cref="AuthenticationRateLimitRule.Window"/>, successful or not. Counters live in process memory.
/// </summary>
public sealed class AuthenticationRateLimitOptions
{
    public const string SectionName = "AuthenticationRateLimiting";

    public AuthenticationRateLimitRule TenantLogin { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule CentralAdminLogin { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule ApiCredentialValidation { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule SignupSubmission { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    public AuthenticationRateLimitRule SignupCheckout { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(10) };

    public AuthenticationRateLimitRule ContactSubmission { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromHours(1) };

    public AuthenticationRateLimitRule SignupDraftSave { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(10) };

    public AuthenticationRateLimitRule SignupDraftCreation { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromHours(1) };

    public AuthenticationRateLimitRule SignupPhoneSend { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromHours(1) };

    public AuthenticationRateLimitRule SignupPhoneVerify { get; set; } = new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) };

    public AuthenticationRateLimitRule SignupAddressAvailability { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule For(string policy) => policy switch
    {
        AuthenticationRateLimitPolicies.TenantLogin => TenantLogin,
        AuthenticationRateLimitPolicies.CentralAdminLogin => CentralAdminLogin,
        AuthenticationRateLimitPolicies.ApiCredentialValidation => ApiCredentialValidation,
        AuthenticationRateLimitPolicies.SignupSubmission => SignupSubmission,
        AuthenticationRateLimitPolicies.SignupCheckout => SignupCheckout,
        AuthenticationRateLimitPolicies.ContactSubmission => ContactSubmission,
        AuthenticationRateLimitPolicies.SignupDraftSave => SignupDraftSave,
        AuthenticationRateLimitPolicies.SignupDraftCreation => SignupDraftCreation,
        AuthenticationRateLimitPolicies.SignupPhoneSend => SignupPhoneSend,
        AuthenticationRateLimitPolicies.SignupPhoneVerify => SignupPhoneVerify,
        AuthenticationRateLimitPolicies.SignupAddressAvailability => SignupAddressAvailability,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown authentication rate-limit policy.")
    };
}

public sealed class AuthenticationRateLimitRule
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }
}

/// <summary>
/// Rejects limits that would disable or break the limiter: no permits, a non-positive or sub-second window, or values
/// beyond what a login limit could reasonably mean. The host does not start with an invalid value.
/// </summary>
public sealed class AuthenticationRateLimitOptionsValidator : IValidateOptions<AuthenticationRateLimitOptions>
{
    public const int MaxPermitLimit = 10_000;
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(1);

    public ValidateOptionsResult Validate(string? name, AuthenticationRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        Check(options.TenantLogin, nameof(AuthenticationRateLimitOptions.TenantLogin), failures);
        Check(options.CentralAdminLogin, nameof(AuthenticationRateLimitOptions.CentralAdminLogin), failures);
        Check(options.ApiCredentialValidation, nameof(AuthenticationRateLimitOptions.ApiCredentialValidation), failures);
        Check(options.SignupSubmission, nameof(AuthenticationRateLimitOptions.SignupSubmission), failures);
        Check(options.SignupCheckout, nameof(AuthenticationRateLimitOptions.SignupCheckout), failures);
        Check(options.ContactSubmission, nameof(AuthenticationRateLimitOptions.ContactSubmission), failures);
        Check(options.SignupDraftSave, nameof(AuthenticationRateLimitOptions.SignupDraftSave), failures);
        Check(options.SignupDraftCreation, nameof(AuthenticationRateLimitOptions.SignupDraftCreation), failures);
        Check(options.SignupPhoneSend, nameof(AuthenticationRateLimitOptions.SignupPhoneSend), failures);
        Check(options.SignupPhoneVerify, nameof(AuthenticationRateLimitOptions.SignupPhoneVerify), failures);
        Check(options.SignupAddressAvailability, nameof(AuthenticationRateLimitOptions.SignupAddressAvailability), failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(AuthenticationRateLimitRule? rule, string policy, ICollection<string> failures)
    {
        var key = $"{AuthenticationRateLimitOptions.SectionName}:{policy}";
        if (rule is null)
        {
            failures.Add($"{key} is required.");
            return;
        }

        if (rule.PermitLimit is < 1 or > MaxPermitLimit)
            failures.Add($"{key}:PermitLimit must be between 1 and {MaxPermitLimit}.");

        if (rule.Window < MinWindow || rule.Window > MaxWindow)
            failures.Add($"{key}:Window must be between {MinWindow:c} and {MaxWindow:c}.");
    }
}
