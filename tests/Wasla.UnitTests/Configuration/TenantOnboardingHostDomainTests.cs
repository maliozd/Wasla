using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;

namespace Wasla.UnitTests.Configuration;

/// <summary>
/// WAS-66: the marketing and application domains are read from <c>Wasla:TenantOnboarding</c> and checked when the
/// process starts, so a deployment cannot run with overlapping or malformed host domains. Configuration is in memory.
/// </summary>
public sealed class TenantOnboardingHostDomainTests
{
    [Theory]
    [InlineData("wasla.tr", "app.wasla.tr")]
    [InlineData("wasla.local", "app.wasla.local")]
    [InlineData("example.test", "orders.example.net")]
    public void SeparateHostNames_AreAccepted(string marketing, string application)
    {
        var options = Bind(("Wasla:TenantOnboarding:MarketingBaseDomain", marketing), ("Wasla:TenantOnboarding:ApplicationBaseDomain", application));

        Assert.Equal(marketing, options.MarketingBaseDomain);
        Assert.Equal(application, options.ApplicationBaseDomain);
    }

    [Fact]
    public void TheDefaults_AreTheDevelopmentDomains()
    {
        var options = Bind();

        Assert.Equal("wasla.local", options.MarketingBaseDomain);
        Assert.Equal("app.wasla.local", options.ApplicationBaseDomain);
    }

    [Theory]
    [InlineData("ApplicationBaseDomain", "https://app.wasla.tr")]
    [InlineData("ApplicationBaseDomain", "app.wasla.tr:443")]
    [InlineData("ApplicationBaseDomain", "app.wasla.tr/auth")]
    [InlineData("ApplicationBaseDomain", "app")]
    [InlineData("ApplicationBaseDomain", "")]
    [InlineData("ApplicationBaseDomain", "app..wasla.tr")]
    [InlineData("ApplicationBaseDomain", "127.0.0.1")]
    [InlineData("MarketingBaseDomain", "https://wasla.tr")]
    [InlineData("MarketingBaseDomain", "localhost")]
    public void AValueThatIsNotAHostName_StopsStartup(string key, string value)
    {
        var error = Assert.Throws<OptionsValidationException>(() => Bind(($"Wasla:TenantOnboarding:{key}", value)));

        Assert.Contains($"Wasla:TenantOnboarding:{key}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wasla.tr", "wasla.tr")]
    [InlineData("wasla.tr", "WWW.wasla.tr")]
    [InlineData("shop.app.wasla.tr", "app.wasla.tr")]
    public void OverlappingMarketingAndApplicationHosts_StopStartup(string marketing, string application)
    {
        Assert.Throws<OptionsValidationException>(() => Bind(
            ("Wasla:TenantOnboarding:MarketingBaseDomain", marketing),
            ("Wasla:TenantOnboarding:ApplicationBaseDomain", application)));
    }

    private static TenantOnboardingOptions Bind(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Platforms:ProviderMode"] = "Mock" })
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddWaslaInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<TenantOnboardingOptions>>().Value;
    }
}
