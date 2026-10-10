using Microsoft.Extensions.Options;

namespace Wasla.Infrastructure.Security.RateLimiting;

/// <summary>Names of the credential-entry rate-limit policies (Wasla.Web logins, Wasla.Api credential check).</summary>
public static class AuthenticationRateLimitPolicies
{
    /// <summary>Tenant Web login, <c>POST /auth/login</c>. Partitioned by resolved tenant and client network.</summary>
    public const string TenantLogin = "auth-tenant-login";

    /// <summary>Central Admin login, <c>POST /admin/login</c>. Partitioned by client network.</summary>
    public const string CentralAdminLogin = "auth-central-admin-login";

    /// <summary>Api credential check, <c>POST /api/auth/validate</c>. Partitioned by resolved tenant and client network.</summary>
    public const string ApiCredentialValidation = "auth-api-credential-validation";

    /// <summary>Whether the policy's partitions are per tenant (the tenant resolved from the host, never from the request body).</summary>
    public static bool IsTenantScoped(string policy) =>
        policy is TenantLogin or ApiCredentialValidation;
}

/// <summary>
/// Limits for the credential-entry endpoints, bound from <c>AuthenticationRateLimiting</c>. Each policy is a fixed window:
/// at most <see cref="AuthenticationRateLimitRule.PermitLimit"/> attempts per partition per
/// <see cref="AuthenticationRateLimitRule.Window"/>, successful or not. Counters live in process memory.
/// </summary>
public sealed class AuthenticationRateLimitOptions
{
    public const string SectionName = "AuthenticationRateLimiting";

    public AuthenticationRateLimitRule TenantLogin { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule CentralAdminLogin { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule ApiCredentialValidation { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) };

    public AuthenticationRateLimitRule For(string policy) => policy switch
    {
        AuthenticationRateLimitPolicies.TenantLogin => TenantLogin,
        AuthenticationRateLimitPolicies.CentralAdminLogin => CentralAdminLogin,
        AuthenticationRateLimitPolicies.ApiCredentialValidation => ApiCredentialValidation,
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
