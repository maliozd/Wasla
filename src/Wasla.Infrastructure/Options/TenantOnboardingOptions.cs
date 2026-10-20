using Microsoft.Extensions.Options;
using Wasla.Application.Onboarding;

namespace Wasla.Infrastructure.Options;

public sealed class TenantOnboardingOptions
{
    public const string SectionName = "Wasla:TenantOnboarding";

    /// <summary>
    /// The public marketing site's domain (<c>wasla.tr</c>; Development <c>wasla.local</c>). It and <c>www.</c> in front
    /// of it serve the marketing pages, signup and checkout.
    /// </summary>
    public string MarketingBaseDomain { get; set; } = "wasla.local";

    /// <summary>
    /// The central application domain (<c>app.wasla.tr</c>; Development <c>app.wasla.local</c>, WAS-66). The domain
    /// itself is the application entry; tenant addresses are exactly <c>{slug}.{ApplicationBaseDomain}</c>.
    /// </summary>
    public string ApplicationBaseDomain { get; set; } = "app.wasla.local";

    /// <summary>trusted or sql:username:password</summary>
    public string SqlAuth { get; set; } = "trusted";

    public int TrialDays { get; set; } = 14;

    /// <summary>How long a pending registration remains valid before expiry.</summary>
    public int PendingRegistrationExpiryDays { get; set; } = 7;

    /// <summary>
    /// How many active registrations (Draft, AwaitingPayment or PaymentSucceeded, not expired) one owner email may have.
    /// A further signup with that email is refused until one is completed, cancelled or expires. Values below 1 count as 1.
    /// </summary>
    public int MaxActivePendingRegistrationsPerOwnerEmail { get; set; } = 3;
}

/// <summary>
/// Refuses to start with host domains that would make the marketing, application and tenant hosts overlap (WAS-66).
/// </summary>
public sealed class TenantOnboardingOptionsValidator : IValidateOptions<TenantOnboardingOptions>
{
    public ValidateOptionsResult Validate(string? name, TenantOnboardingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var marketing = ValidateDomain(nameof(TenantOnboardingOptions.MarketingBaseDomain), options.MarketingBaseDomain, failures);
        var application = ValidateDomain(nameof(TenantOnboardingOptions.ApplicationBaseDomain), options.ApplicationBaseDomain, failures);

        if (marketing is not null && application is not null)
        {
            if (application == marketing || application == "www." + marketing)
                failures.Add($"{TenantOnboardingOptions.SectionName}:ApplicationBaseDomain must differ from the marketing host and its www. host.");
            if (marketing.EndsWith("." + application, StringComparison.Ordinal))
                failures.Add($"{TenantOnboardingOptions.SectionName}:MarketingBaseDomain must not be a host under ApplicationBaseDomain.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The normalized domain, or null after adding a failure: a DNS name with at least two labels, no scheme, port or path.</summary>
    private static string? ValidateDomain(string key, string? value, List<string> failures)
    {
        var domain = WaslaHostClassifier.NormalizeDomain(value);
        if (domain.Length == 0
            || !domain.Contains('.')
            || Uri.CheckHostName(domain) != UriHostNameType.Dns
            || domain.Contains(".."))
        {
            failures.Add($"{TenantOnboardingOptions.SectionName}:{key} must be a host name such as example.com, without a scheme, port or path.");
            return null;
        }

        return domain;
    }
}
