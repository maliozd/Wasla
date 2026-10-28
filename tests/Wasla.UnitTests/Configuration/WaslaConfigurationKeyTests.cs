using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Wasla.Cli;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Options;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Web.PrintBridge;

namespace Wasla.UnitTests.Configuration;

/// <summary>
/// WAS-136: Web, Api, Worker and the CLI read their onboarding, tenant database, URL and Print Bridge download settings
/// only from <c>Wasla:*</c> and <c>TenantDb:*</c>. These tests prove the current state positively: every key the
/// committed configuration sets is an approved current key, the registrations bind exactly the approved sections, the
/// onboarding options bind from <c>Wasla:TenantOnboarding</c>, and the deployment documentation names only approved
/// keys. All configuration is in memory or read from the committed files; no database is reached.
/// </summary>
public sealed partial class WaslaConfigurationKeyTests
{
    private const string LocalDb = "(localdb)\\MSSQLLocalDB";

    /// <summary>Every leaf key a committed Web, Api, Worker or CLI <c>appsettings*.json</c> may set.</summary>
    private static readonly HashSet<string> ApprovedCommittedKeys = new(StringComparer.Ordinal)
    {
        "AllowedHosts",
        "ConnectionStrings:CentralDb",
        "DetailedErrors",
        "DevelopmentTools:EnableTenantReset",
        "Email:EnableSsl",
        "Email:FromEmail",
        "Email:FromName",
        "Email:Provider",
        "Email:SmtpHost",
        "Email:SmtpPassword",
        "Email:SmtpPort",
        "Email:SmtpUsername",
        "Logging:Console:FormatterOptions:IncludeScopes",
        "Platform:TrendyolGo:AgentName",
        "Platform:TrendyolGo:BaseUrl",
        "Platform:TrendyolGo:RequestTimeout",
        "Platform:Yemeksepeti:BaseUrl",
        "Platform:Yemeksepeti:DefaultPageSize",
        "Platform:Yemeksepeti:RequestTimeoutSeconds",
        "Platform:Yemeksepeti:TokenPath",
        "Platforms:ProviderMode",
        "Serilog:MinimumLevel:Default",
        "TenantDb:ServerInstance",
        "Wasla:ApiBaseUrl",
        "Wasla:Contact:RecipientEmail",
        "Wasla:PrintBridgeDownload:PackageFileName",
        "Wasla:PrintBridgeDownload:PackagePath",
        "Wasla:TenantOnboarding:ApplicationBaseDomain",
        "Wasla:TenantOnboarding:MarketingBaseDomain",
        "Wasla:TenantOnboarding:SqlAuth",
        "Wasla:TenantOnboarding:TrialDays"
    };

    /// <summary>Logging category names are open-ended; their levels live under these sections.</summary>
    private static readonly string[] ApprovedCommittedPrefixes = ["Logging:LogLevel:", "Serilog:MinimumLevel:Override:"];

    /// <summary>The setting names that belong to onboarding, the tenant database, public URLs or the package download.</summary>
    private static readonly HashSet<string> WaslaOwnedSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MarketingBaseDomain", "ApplicationBaseDomain", "SqlAuth", "TrialDays", "PendingRegistrationExpiryDays",
        "MaxActivePendingRegistrationsPerOwnerEmail", "PublicSignupEnabled", "ServerInstance", "ApiBaseUrl", "TenantWebBaseUrl",
        "PublicWebBaseUrl", "PackageFileName", "PackagePath"
    };

    private static readonly string[] HostProjects = ["Wasla.Api", "Wasla.Cli", "Wasla.Web", "Wasla.Worker"];

    // --- Committed configuration --------------------------------------------------------------------

    [Fact]
    public void EveryCommittedAppSettingsKey_IsAnApprovedCurrentKey()
    {
        var files = CommittedAppSettingsFiles();
        Assert.Equal(11, files.Count);

        foreach (var (file, keys) in files)
        {
            foreach (var key in keys)
            {
                Assert.True(
                    ApprovedCommittedKeys.Contains(key) || ApprovedCommittedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)),
                    $"{file} sets {key}, which is not an approved configuration key.");
            }
        }
    }

    [Fact]
    public void OnboardingTenantDbUrlAndDownloadKeys_AreCommittedOnlyUnderWaslaOrTenantDb()
    {
        var owned = CommittedAppSettingsFiles()
            .SelectMany(f => f.Keys.Select(key => (f.File, Key: key)))
            .Where(entry => WaslaOwnedSettingNames.Contains(entry.Key.Split(':')[^1]))
            .ToList();

        Assert.NotEmpty(owned);
        Assert.All(owned, entry => Assert.True(
            entry.Key.StartsWith("Wasla:", StringComparison.Ordinal) || entry.Key.StartsWith("TenantDb:", StringComparison.Ordinal),
            $"{entry.File} sets {entry.Key} outside Wasla:* and TenantDb:*."));
    }

    [Fact]
    public void CommittedWebConfiguration_SetsTheWaslaKeys()
    {
        var web = Load("Wasla.Web", "appsettings.json");

        Assert.Equal("http://localhost:59451", web["Wasla:ApiBaseUrl"]);
        // The host domains are set only for Development; elsewhere the environment must supply them (WAS-66).
        Assert.Null(web["Wasla:TenantOnboarding:MarketingBaseDomain"]);
        Assert.Null(web["Wasla:TenantOnboarding:ApplicationBaseDomain"]);
        var development = Load("Wasla.Web", "appsettings.json", "appsettings.Development.json");
        Assert.Equal("wasla.local", development["Wasla:TenantOnboarding:MarketingBaseDomain"]);
        Assert.Equal("app.wasla.local", development["Wasla:TenantOnboarding:ApplicationBaseDomain"]);
        Assert.Equal("trusted", web["Wasla:TenantOnboarding:SqlAuth"]);
        Assert.Equal("14", web["Wasla:TenantOnboarding:TrialDays"]);
        Assert.Equal(PrintBridgePackagePaths.DefaultPackageFileName, web["Wasla:PrintBridgeDownload:PackageFileName"]);
        Assert.Equal(string.Empty, web["Wasla:PrintBridgeDownload:PackagePath"]);
        Assert.Equal(PrintBridgePackagePaths.DefaultPackageFileName, PrintBridgePackagePaths.GetPackageFileName(web));
        // Contact form messages are emailed to the Wasla team (Wasla:Contact).
        Assert.Equal("mehmetaliozdemir@wasla.tr", web["Wasla:Contact:RecipientEmail"]);
    }

    [Theory]
    [InlineData("Wasla.Web")]
    [InlineData("Wasla.Cli")]
    public void CommittedDevelopmentConfiguration_NamesLocalDbAsTheTenantSqlServer_AndTheBaseFileNamesNone(string project)
    {
        Assert.Null(Load(project, "appsettings.json")["TenantDb:ServerInstance"]);
        Assert.Equal(LocalDb, Load(project, "appsettings.json", "appsettings.Development.json")["TenantDb:ServerInstance"]);
    }

    [Fact]
    public void LaunchSettings_SetOnlyTheHostEnvironmentVariables()
    {
        var files = Directory.GetFiles(Path.Combine(RepositoryRoot(), "src"), "launchSettings.json", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToList();
        Assert.Equal(3, files.Count);

        var variables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var document = ParseJson(file);
            foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateObject())
            {
                if (profile.Value.TryGetProperty("environmentVariables", out var environment))
                    variables.UnionWith(environment.EnumerateObject().Select(p => p.Name));
            }
        }

        Assert.Equal(new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" }, variables);
    }

    // --- Registrations ------------------------------------------------------------------------------

    [Fact]
    public void AddWaslaInfrastructure_BindsExactlyTheApprovedSections()
    {
        var configuration = new RecordingConfiguration(Memory(("Platforms:ProviderMode", "Mock")));

        new ServiceCollection().AddWaslaInfrastructure(configuration);

        Assert.Equal(
            new[] { "Email", "Platform:TrendyolGo", "Platform:Yemeksepeti", "Platforms:ProviderMode", "TrendyolGoMealWebhookPilot", "Wasla:TenantOnboarding" },
            configuration.Paths);
    }

    [Fact]
    public void CliServices_BindExactlyTheApprovedSections()
    {
        var configuration = new RecordingConfiguration(Memory());

        new ServiceCollection().AddWaslaCliServices(configuration);

        Assert.Equal(new[] { "Email", "Wasla:TenantOnboarding" }, configuration.Paths);
    }

    [Fact]
    public void TenantOnboardingOptions_BindFromWaslaTenantOnboarding_InWebAndCli()
    {
        var values = new (string, string)[]
        {
            ("Platforms:ProviderMode", "Mock"),
            ("Wasla:TenantOnboarding:MarketingBaseDomain", "example.test"),
            ("Wasla:TenantOnboarding:ApplicationBaseDomain", "orders.example.test"),
            ("Wasla:TenantOnboarding:SqlAuth", "sql:test-user:not-a-real-password"),
            ("Wasla:TenantOnboarding:TrialDays", "30"),
            ("Wasla:TenantOnboarding:PendingRegistrationExpiryDays", "5"),
            ("Wasla:TenantOnboarding:MaxActivePendingRegistrationsPerOwnerEmail", "2"),
            ("Wasla:TenantOnboarding:PublicSignupEnabled", "false")
        };

        foreach (var register in new Action<IServiceCollection, IConfiguration>[]
                 {
                     (services, configuration) => services.AddWaslaInfrastructure(configuration),
                     (services, configuration) => services.AddWaslaCliServices(configuration)
                 })
        {
            var services = new ServiceCollection();
            register(services, Memory(values));
            using var provider = services.BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<TenantOnboardingOptions>>().Value;

            Assert.Equal("Wasla:TenantOnboarding", TenantOnboardingOptions.SectionName);
            Assert.Equal("example.test", options.MarketingBaseDomain);
            Assert.Equal("orders.example.test", options.ApplicationBaseDomain);
            Assert.Equal("sql:test-user:not-a-real-password", options.SqlAuth);
            Assert.Equal(30, options.TrialDays);
            Assert.Equal(5, options.PendingRegistrationExpiryDays);
            Assert.Equal(2, options.MaxActivePendingRegistrationsPerOwnerEmail);
            Assert.False(options.PublicSignupEnabled);
        }
    }

    [Fact]
    public void TenantOnboardingOptions_FromTheCommittedWebConfiguration_KeepTheirValues()
    {
        var configuration = Load("Wasla.Web", "appsettings.json", "appsettings.Development.json");
        var services = new ServiceCollection();
        services.AddWaslaInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantOnboardingOptions>>().Value;

        Assert.Equal("wasla.local", options.MarketingBaseDomain);
        Assert.Equal("app.wasla.local", options.ApplicationBaseDomain);
        Assert.Equal("trusted", options.SqlAuth);
        Assert.Equal(14, options.TrialDays);
        Assert.Equal(7, options.PendingRegistrationExpiryDays);
        Assert.Equal(3, options.MaxActivePendingRegistrationsPerOwnerEmail);
        // Open unless a deployment closes it (WAS-76); the committed files do not set it.
        Assert.True(options.PublicSignupEnabled);
    }

    // --- EF design time -----------------------------------------------------------------------------

    [Fact]
    public void TenantDesignTime_UsesTheTenantDbDesignTimeConnectionString_WhenSet()
    {
        const string designTime = "Server=design-time.test;Database=Wasla_Tenant_Scratch;Trusted_Connection=True";
        var configuration = Memory(
            ("ConnectionStrings:TenantDbDesignTime", designTime),
            ("ConnectionStrings:CentralDb", "Server=central.test;Database=Wasla_Central;Trusted_Connection=True"));

        Assert.Equal(designTime, TenantDesignTimeDbContextFactory.ResolveConnectionString(configuration));
    }

    [Fact]
    public void TenantDesignTime_DerivesTheWaslaScratchCatalog_FromCentralDb()
    {
        var configuration = Memory(("ConnectionStrings:CentralDb", "Server=central.test;Database=Wasla_Central;Trusted_Connection=True"));

        var derived = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            TenantDesignTimeDbContextFactory.ResolveConnectionString(configuration));

        Assert.Equal("central.test", derived.DataSource);
        Assert.Equal("Wasla_Tenant_DesignTime", derived.InitialCatalog);
    }

    [Fact]
    public void TenantDesignTime_WithoutEither_NamesTheTenantDbDesignTimeKey()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TenantDesignTimeDbContextFactory.ResolveConnectionString(Memory()));

        Assert.Contains("ConnectionStrings:TenantDbDesignTime", error.Message, StringComparison.Ordinal);
    }

    // --- Documentation ------------------------------------------------------------------------------

    [Fact]
    public void DeploymentCheatSheet_ListsOnlyApprovedKeys()
    {
        var deployment = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operations", "deployment.md"));
        var sheet = CheatSheetPattern().Match(deployment);
        Assert.True(sheet.Success, "deployment.md has no environment variable cheat sheet.");

        var variables = sheet.Groups[1].Value
            .Split('\n')
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split([' ', '/', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        Assert.Contains("TenantDb__ServerInstance", variables);
        Assert.Contains("Wasla__TenantOnboarding__MarketingBaseDomain", variables);
        Assert.Contains("Wasla__TenantOnboarding__ApplicationBaseDomain", variables);
        Assert.All(variables, variable => Assert.True(IsApprovedDocumentedKey(ToKey(variable)), $"deployment.md lists {variable}."));
    }

    [Theory]
    [InlineData("operations", "deployment.md")]
    [InlineData("operations", "cli.md")]
    [InlineData("operations", "local-development.md")]
    [InlineData("operations", "migrations.md")]
    [InlineData("architecture", "tenancy.md")]
    public void OperationsDocs_NameOnlyApprovedWaslaAndTenantDbKeys(string folder, string file)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", folder, file));
        var names = DocumentedKeyPattern().Matches(text).Select(m => m.Value).Distinct().ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(IsApprovedDocumentedKey(ToKey(name)), $"{file} names {name}."));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    /// <summary>Keys an operator may set for Web, Api, Worker or the CLI, as the deployment documentation names them.</summary>
    private static bool IsApprovedDocumentedKey(string key)
    {
        var approved = new HashSet<string>(StringComparer.Ordinal)
        {
            "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ENCRYPTION_MASTER_KEY",
            "ConnectionStrings:CentralDb", "ConnectionStrings:TenantDbDesignTime",
            "TenantDb:ServerInstance",
            "Wasla:ApiBaseUrl", "Wasla:TenantWebBaseUrl", "Wasla:PublicWebBaseUrl", "Wasla:Contact:RecipientEmail",
            "Wasla:PrintBridgeDownload:PackageFileName", "Wasla:PrintBridgeDownload:PackagePath",
            "DataProtection:KeyPath",
            "Platforms:ProviderMode", "Platform:TrendyolGo:BaseUrl", "Platform:Yemeksepeti:BaseUrl",
            "TrustedProxies:KnownProxies",
            "AuthenticationRateLimiting:TenantLogin:PermitLimit",
            "TenantResolution:MissCache:Ttl",
            "Realtime:Internal:Port", "Realtime:Internal:Secret", "Realtime:Internal:Disabled", "Realtime:Internal:TimeoutSeconds",
            "Realtime:SessionRevalidationSeconds"
        };
        approved.UnionWith(typeof(TenantOnboardingOptions).GetProperties().Select(p => $"{TenantOnboardingOptions.SectionName}:{p.Name}"));

        return approved.Contains(key) || approved.Any(a => a.StartsWith(key + ":", StringComparison.Ordinal));
    }

    /// <summary>The configuration key an environment variable maps to; a trailing array index names the list itself.</summary>
    private static string ToKey(string name) =>
        Regex.Replace(name.Replace("__", ":", StringComparison.Ordinal), @"(:\d+)+$", string.Empty);

    [GeneratedRegex(@"## Environment variable cheat sheet.*?```text\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CheatSheetPattern();

    [GeneratedRegex(@"\b(?:Wasla|TenantDb|ConnectionStrings)(?:__|:)[A-Za-z][A-Za-z0-9]*(?:(?:__|:)[A-Za-z0-9]+)*")]
    private static partial Regex DocumentedKeyPattern();

    private static List<(string File, List<string> Keys)> CommittedAppSettingsFiles()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return HostProjects
            .SelectMany(project => Directory.GetFiles(Path.Combine(src, project), "appsettings*.json", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .Select(path => (
                Path.GetRelativePath(src, path),
                new ConfigurationBuilder().AddJsonFile(path).Build().AsEnumerable()
                    .Where(pair => pair.Value is not null)
                    .Select(pair => pair.Key)
                    .ToList()))
            .ToList();
    }

    private static IConfiguration Load(string project, params string[] files)
    {
        var builder = new ConfigurationBuilder().SetBasePath(Path.Combine(RepositoryRoot(), "src", project));
        foreach (var file in files)
            builder.AddJsonFile(file, optional: false);
        return builder.Build();
    }

    private static IConfiguration Memory(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static JsonDocument ParseJson(string path) =>
        JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Wasla.sln not found above the test output directory.");
    }

    /// <summary>Records every path a registration reads or binds directly from the configuration root.</summary>
    private sealed class RecordingConfiguration(IConfiguration inner) : IConfiguration
    {
        public SortedSet<string> Paths { get; } = new(StringComparer.Ordinal);

        public string? this[string key]
        {
            get
            {
                Paths.Add(key);
                return inner[key];
            }
            set => inner[key] = value;
        }

        public IConfigurationSection GetSection(string key)
        {
            Paths.Add(key);
            return inner.GetSection(key);
        }

        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();

        public IChangeToken GetReloadToken() => inner.GetReloadToken();
    }
}
