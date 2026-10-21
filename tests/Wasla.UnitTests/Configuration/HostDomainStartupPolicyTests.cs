using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.UnitTests.DevelopmentTools;

namespace Wasla.UnitTests.Configuration;

/// <summary>
/// WAS-66: what Wasla.Web and Wasla.Api do with the marketing and application domains at startup. Outside Development
/// both must be set to public host names: a missing value would fall back to the Development domains, and a
/// Development-only name (<c>.local</c>, <c>localhost</c>, reserved names, IP addresses) would generate links nobody can
/// open, so either refuses to start with a message that names the key. Development starts with
/// <c>wasla.local</c> and <c>app.wasla.local</c>. The committed configuration files are checked as each environment
/// loads them. No secret is involved.
/// </summary>
public sealed class HostDomainStartupPolicyTests
{
    public static TheoryData<string> NonDevelopmentEnvironments => new() { Environments.Production, Environments.Staging, "Pilot" };

    [Theory]
    [MemberData(nameof(NonDevelopmentEnvironments))]
    public void OutsideDevelopment_WithoutTheDomains_RefusesToStart_AndNamesBothKeys(string environment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Register(environment));

        Assert.Contains("Wasla:TenantOnboarding:MarketingBaseDomain is not set", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wasla:TenantOnboarding:ApplicationBaseDomain is not set", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wasla__TenantOnboarding__MarketingBaseDomain", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wasla__TenantOnboarding__ApplicationBaseDomain", error.Message, StringComparison.Ordinal);
        Assert.Contains(environment, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wasla.local", "app.wasla.local")]
    [InlineData("wasla.tr", "app.wasla.local")]
    [InlineData("wasla.localhost", "app.wasla.tr")]
    [InlineData("wasla.test", "app.wasla.test")]
    [InlineData("wasla.example", "app.wasla.tr")]
    [InlineData("wasla.tr", "app.wasla.internal")]
    [InlineData("wasla.tr", "app.home.arpa")]
    public void Production_WithADevelopmentOnlyDomain_RefusesToStart(string marketing, string application)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Register(Environments.Production, marketing, application));

        Assert.Contains("Development-only name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", "app.wasla.tr")]
    [InlineData("127.0.0.1", "app.wasla.tr")]
    [InlineData("https://wasla.tr", "app.wasla.tr")]
    [InlineData("wasla.tr", "wasla.tr")]
    public void Production_WithAnInvalidOrOverlappingDomain_RefusesToStart(string marketing, string application)
    {
        Assert.Throws<InvalidOperationException>(() => Register(Environments.Production, marketing, application));
    }

    [Theory]
    [MemberData(nameof(NonDevelopmentEnvironments))]
    public void OutsideDevelopment_WithPublicDomains_Starts(string environment)
    {
        Register(environment, "wasla.tr", "app.wasla.tr");
        Register(environment, "staging.wasla.tr", "app.staging.wasla.tr");
    }

    [Fact]
    public void Development_WithoutTheDomains_StartsWithTheDevelopmentDefaults()
    {
        Register(Environments.Development);
        Register(Environments.Development, "wasla.local", "app.wasla.local");
    }

    [Fact]
    public void Development_WithInvalidDomains_StillRefusesToStart()
    {
        Assert.Throws<InvalidOperationException>(() => Register(Environments.Development, "wasla.local", "wasla.local"));
    }

    // --- The committed configuration files ------------------------------------------------------------

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Api")]
    public void CommittedConfiguration_InProduction_WithoutEnvironmentVariables_RefusesToStart(string project)
    {
        var configuration = Load(project, Environments.Production);

        var error = Assert.Throws<InvalidOperationException>(() =>
            HostDomainRegistration.ThrowIfNotAllowed(configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("is not set", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Api")]
    public void CommittedConfiguration_InProduction_WithThePublicDomainsFromTheEnvironment_Starts(string project)
    {
        var configuration = Load(
            project,
            Environments.Production,
            ("Wasla:TenantOnboarding:MarketingBaseDomain", "wasla.tr"),
            ("Wasla:TenantOnboarding:ApplicationBaseDomain", "app.wasla.tr"));

        HostDomainRegistration.ThrowIfNotAllowed(configuration, new TestHostEnvironment(Environments.Production));
    }

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Api")]
    public void CommittedConfiguration_InDevelopment_UsesTheDevelopmentDomains(string project)
    {
        var configuration = Load(project, Environments.Development);

        HostDomainRegistration.ThrowIfNotAllowed(configuration, new TestHostEnvironment(Environments.Development));
        Assert.Equal("wasla.local", configuration["Wasla:TenantOnboarding:MarketingBaseDomain"]);
        Assert.Equal("app.wasla.local", configuration["Wasla:TenantOnboarding:ApplicationBaseDomain"]);
    }

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Api")]
    public void TheBaseConfiguration_NamesNoDomain(string project)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", project, "appsettings.json"), optional: false)
            .Build();

        Assert.Null(configuration["Wasla:TenantOnboarding:MarketingBaseDomain"]);
        Assert.Null(configuration["Wasla:TenantOnboarding:ApplicationBaseDomain"]);
    }

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Api")]
    public void WebAndApi_ApplyThePolicy_WithTheirOwnEnvironment(string project)
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", project, "Program.cs"));

        Assert.Contains("builder.Services.AddWaslaHostDomains(builder.Configuration, builder.Environment);", program, StringComparison.Ordinal);
    }

    // --- Helpers --------------------------------------------------------------------------------------

    private static void Register(string environment, string? marketing = null, string? application = null)
    {
        var values = new Dictionary<string, string?>();
        if (marketing is not null)
            values["Wasla:TenantOnboarding:MarketingBaseDomain"] = marketing;
        if (application is not null)
            values["Wasla:TenantOnboarding:ApplicationBaseDomain"] = application;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        new ServiceCollection().AddWaslaHostDomains(configuration, new TestHostEnvironment(environment));
    }

    /// <summary>The project's committed files as that environment loads them, then values an operator sets.</summary>
    private static IConfiguration Load(string project, string environment, params (string Key, string Value)[] environmentVariables)
    {
        var folder = Path.Combine(RepositoryRoot(), "src", project);
        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(folder, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(folder, $"appsettings.{environment}.json"), optional: true)
            .AddInMemoryCollection(environmentVariables.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Wasla.sln was not found.");
    }
}
