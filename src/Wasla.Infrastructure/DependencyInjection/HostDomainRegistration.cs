using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Onboarding;
using Wasla.Infrastructure.Options;

namespace Wasla.Infrastructure.DependencyInjection;

public static class HostDomainRegistration
{
    /// <summary>
    /// Top-level names that never belong to a public deployment: loopback, mDNS and the names reserved for
    /// documentation, testing and private networks. Development uses <c>wasla.local</c> and <c>app.wasla.local</c>.
    /// </summary>
    private static readonly string[] DevelopmentOnlySuffixes =
        ["localhost", "local", "test", "example", "invalid", "internal", "lan", "home.arpa"];

    /// <summary>
    /// The host-domain startup policy of Wasla.Web and Wasla.Api (WAS-66). Outside Development both
    /// <c>Wasla:TenantOnboarding:MarketingBaseDomain</c> and <c>ApplicationBaseDomain</c> must be set explicitly to
    /// public host names; a missing value (which would fall back to the Development domains) or a Development-only name
    /// refuses to start. In every environment the domains must be valid and must not overlap
    /// (<see cref="TenantOnboardingOptionsValidator"/>). Wasla.Worker and the CLI build no links and do not call this.
    /// </summary>
    public static IServiceCollection AddWaslaHostDomains(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ThrowIfNotAllowed(configuration, environment);
        return services;
    }

    public static void ThrowIfNotAllowed(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection(TenantOnboardingOptions.SectionName);
        var options = new TenantOnboardingOptions();
        section.Bind(options);

        var failures = new List<string>();
        if (!environment.IsDevelopment())
        {
            CheckPublicDomain(section, nameof(TenantOnboardingOptions.MarketingBaseDomain), "wasla.tr", environment, failures);
            CheckPublicDomain(section, nameof(TenantOnboardingOptions.ApplicationBaseDomain), "app.wasla.tr", environment, failures);
        }

        var result = new TenantOnboardingOptionsValidator().Validate(name: null, options);
        if (result.Failed)
            failures.AddRange(result.Failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                string.Join(" ", failures) + " See docs/operations/deployment.md#domains-and-https.");
        }
    }

    private static void CheckPublicDomain(
        IConfigurationSection section,
        string key,
        string productionExample,
        IHostEnvironment environment,
        List<string> failures)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(
                $"{TenantOnboardingOptions.SectionName}:{key} is not set, which is allowed only in Development " +
                $"(environment: {environment.EnvironmentName}). Set Wasla__TenantOnboarding__{key} " +
                $"(production: {productionExample}).");
            return;
        }

        var domain = WaslaHostClassifier.NormalizeDomain(value);
        if (IsDevelopmentOnly(domain))
        {
            failures.Add(
                $"{TenantOnboardingOptions.SectionName}:{key} is '{domain}', a Development-only name that is not allowed " +
                $"outside Development (environment: {environment.EnvironmentName}). Set the public host name " +
                $"(production: {productionExample}).");
        }
    }

    /// <summary>True for a loopback, mDNS or reserved name (see <see cref="DevelopmentOnlySuffixes"/>).</summary>
    public static bool IsDevelopmentOnly(string domain)
    {
        var normalized = WaslaHostClassifier.NormalizeDomain(domain);
        return DevelopmentOnlySuffixes.Any(suffix =>
            normalized == suffix || normalized.EndsWith("." + suffix, StringComparison.Ordinal));
    }
}
