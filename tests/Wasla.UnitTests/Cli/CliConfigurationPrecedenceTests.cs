using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Wasla.Cli;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-93: in the CLI, environment variables (and the command line) override the JSON files, and the committed base
/// <c>appsettings.json</c> names no CentralDb or tenant SQL Server. The CLI used to add <c>appsettings.json</c> again
/// after the host's defaults, so every key in it beat a deployment's environment variables.
///
/// The first group runs <see cref="CliConfiguration"/> on temporary JSON files with real environment variables under a
/// unique prefix. The second runs a copy of the built CLI as a child process with its own JSON files and environment;
/// its CentralDb values are whitespace or an unreachable local port, so no database is ever reached.
/// </summary>
public sealed class CliConfigurationPrecedenceTests : IDisposable
{
    private const string Unreachable = "Server=tcp:127.0.0.1,1;Database=WaslaPrecedenceProbe;Connect Timeout=1;ConnectRetryCount=0;Trusted_Connection=True;TrustServerCertificate=True";
    private const string NotConfigured = "ConnectionStrings:CentralDb is not configured.";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"wasla-cli-config-{Guid.NewGuid():N}");
    private readonly string _prefix = $"WASLA_TEST_{Guid.NewGuid():N}_";
    private readonly List<string> _variables = new();

    public CliConfigurationPrecedenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var name in _variables)
            Environment.SetEnvironmentVariable(name, null);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // --- Source order -------------------------------------------------------------------------------

    [Fact]
    public void AnEnvironmentVariable_OverridesTheBaseJson()
    {
        WriteJson("appsettings.json", """{ "ConnectionStrings": { "CentralDb": "from-base-json" } }""");
        SetVariable("ConnectionStrings__CentralDb", "from-environment");

        Assert.Equal("from-environment", Build("Production").GetConnectionString("CentralDb"));
    }

    [Fact]
    public void AnEnvironmentVariable_OverridesTheEnvironmentJson()
    {
        WriteJson("appsettings.json", "{}");
        WriteJson("appsettings.Development.json", """{ "ConnectionStrings": { "CentralDb": "from-development-json" } }""");
        SetVariable("ConnectionStrings__CentralDb", "from-environment");

        Assert.Equal("from-environment", Build("Development").GetConnectionString("CentralDb"));
    }

    [Fact]
    public void AWhitespaceEnvironmentVariable_StillOverridesJson()
    {
        WriteJson("appsettings.json", """{ "ConnectionStrings": { "CentralDb": "from-base-json" } }""");
        SetVariable("ConnectionStrings__CentralDb", "   ");

        Assert.True(string.IsNullOrWhiteSpace(Build("Production").GetConnectionString("CentralDb")));
    }

    [Fact]
    public void TheEnvironmentJson_OverridesTheBaseJson_AndIsReadOnlyForItsEnvironment()
    {
        WriteJson("appsettings.json", """{ "ConnectionStrings": { "CentralDb": "from-base-json" } }""");
        WriteJson("appsettings.Development.json", """{ "ConnectionStrings": { "CentralDb": "from-development-json" } }""");

        Assert.Equal("from-development-json", Build("Development").GetConnectionString("CentralDb"));
        Assert.Equal("from-base-json", Build("Production").GetConnectionString("CentralDb"));
    }

    [Fact]
    public void TheCommandLine_OverridesEnvironmentVariables()
    {
        WriteJson("appsettings.json", "{}");
        SetVariable("ConnectionStrings__CentralDb", "from-environment");

        var configuration = Build("Production", "--ConnectionStrings:CentralDb", "from-command-line");

        Assert.Equal("from-command-line", configuration.GetConnectionString("CentralDb"));
    }

    [Fact]
    public void TheJsonFiles_AreReadFromTheGivenDirectory_NotTheWorkingDirectory()
    {
        WriteJson("appsettings.json", """{ "ConnectionStrings": { "CentralDb": "from-cli-directory" } }""");

        Assert.NotEqual(Path.GetFullPath(_directory), Path.GetFullPath(Directory.GetCurrentDirectory()));
        Assert.Equal("from-cli-directory", Build("Production").GetConnectionString("CentralDb"));
    }

    // --- Tenant SQL Server --------------------------------------------------------------------------

    [Fact]
    public void TenantSqlServer_FromTheEnvironment_OverridesJson_AndTheOverrideStillWins()
    {
        WriteJson("appsettings.json", """
            { "CustomerDb": { "ServerInstance": "json-customerdb" },
              "OrderHub": { "CustomerOnboarding": { "ServerInstance": "json-onboarding" } } }
            """);
        SetVariable("CustomerDb__ServerInstance", "env-customerdb");
        var configuration = Build("Production");

        Assert.Equal("env-customerdb", Resolve(configuration, overrideServer: null).Server);
        Assert.Equal("cli-override", Resolve(configuration, overrideServer: "cli-override").Server);
    }

    [Fact]
    public void TenantSqlServer_OnboardingKeyFromTheEnvironment_IsUsedWhenCustomerDbIsUnset()
    {
        WriteJson("appsettings.json", """{ "OrderHub": { "CustomerOnboarding": { "ServerInstance": "json-onboarding" } } }""");
        SetVariable("OrderHub__CustomerOnboarding__ServerInstance", "env-onboarding");

        Assert.Equal("env-onboarding", Resolve(Build("Production"), overrideServer: null).Server);
    }

    [Fact]
    public void TenantSqlServer_IsMissing_WhenNothingIsConfigured()
    {
        WriteJson("appsettings.json", "{}");

        var resolution = Resolve(Build("Production"), overrideServer: null);

        Assert.False(resolution.IsResolved);
        Assert.Equal(TenantSqlServerConfiguration.MissingMessage, resolution.Error);
    }

    // --- Committed files ----------------------------------------------------------------------------

    [Fact]
    public void TheCommittedBaseJson_NamesNoCentralDbOrTenantServer_AndDevelopmentNamesBothExplicitly()
    {
        var cli = Path.Combine(RepositoryRoot(), "src", "Wasla.Cli");
        var production = new ConfigurationBuilder().AddJsonFile(Path.Combine(cli, "appsettings.json")).Build();
        var development = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(cli, "appsettings.json"))
            .AddJsonFile(Path.Combine(cli, "appsettings.Development.json"))
            .Build();

        Assert.Null(production.GetConnectionString("CentralDb"));
        Assert.Null(production[TenantSqlServerConfiguration.CustomerDbKey]);
        Assert.Null(production[TenantSqlServerConfiguration.OnboardingKey]);
        Assert.Contains("(localdb)", development.GetConnectionString("CentralDb"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("(localdb)\\MSSQLLocalDB", development[TenantSqlServerConfiguration.CustomerDbKey]);
    }

    // --- The CLI binary -----------------------------------------------------------------------------

    [Fact]
    public async Task Cli_WithoutCentralDb_FailsBeforeAnyDatabaseAccess()
    {
        var cli = CopyCli(
            baseJson: File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Wasla.Cli", "appsettings.json")),
            developmentJson: null);

        var run = await RunCliAsync(cli, environment: "Production", centralDbVariable: null);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(NotConfigured, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_AWhitespaceEnvironmentVariable_OverridesACentralDbInTheBaseJson()
    {
        // Were the JSON to win, the CLI would try the unreachable server and fail with exit code 1.
        var cli = CopyCli(baseJson: CentralDbJson(Unreachable), developmentJson: null);

        var run = await RunCliAsync(cli, environment: "Production", centralDbVariable: "   ");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(NotConfigured, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_AWhitespaceEnvironmentVariable_OverridesACentralDbInTheDevelopmentJson()
    {
        var cli = CopyCli(baseJson: "{}", developmentJson: CentralDbJson(Unreachable));

        var run = await RunCliAsync(cli, environment: "Development", centralDbVariable: "   ");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(NotConfigured, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ACentralDbEnvironmentVariable_OverridesAnEmptyValueInTheBaseJson()
    {
        // Were the JSON to win, the CLI would stop with "not configured" (exit code 2). The environment's value wins,
        // so the command runs and fails on the unreachable server instead.
        var cli = CopyCli(baseJson: CentralDbJson("   "), developmentJson: null);

        var run = await RunCliAsync(cli, environment: "Production", centralDbVariable: Unreachable);

        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain(NotConfigured, run.Output, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private IConfiguration Build(string environmentName, params string[] args)
    {
        var builder = new ConfigurationBuilder();
        CliConfiguration.Configure(builder, _directory, environmentName, args, userSecretsAssembly: null, environmentVariablePrefix: _prefix);
        return builder.Build();
    }

    private static TenantSqlServerResolution Resolve(IConfiguration configuration, string? overrideServer) =>
        TenantSqlServerConfiguration.Resolve(
            overrideServer,
            configuration[TenantSqlServerConfiguration.CustomerDbKey],
            configuration[TenantSqlServerConfiguration.OnboardingKey]);

    private void WriteJson(string fileName, string json) => File.WriteAllText(Path.Combine(_directory, fileName), json);

    private void SetVariable(string name, string value)
    {
        var full = _prefix + name;
        _variables.Add(full);
        Environment.SetEnvironmentVariable(full, value);
    }

    private static string CentralDbJson(string value) =>
        "{ \"ConnectionStrings\": { \"CentralDb\": \"" + value.Replace("\\", "\\\\") + "\" } }";

    /// <summary>A private copy of the built CLI with the given JSON files, so the shared build output is never changed.</summary>
    private string CopyCli(string baseJson, string? developmentJson)
    {
        var source = CliOutputDirectory();
        var target = Path.Combine(_directory, "cli");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (Path.GetFileName(relative).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        File.WriteAllText(Path.Combine(target, "appsettings.json"), baseJson);
        if (developmentJson is not null)
            File.WriteAllText(Path.Combine(target, "appsettings.Development.json"), developmentJson);
        return target;
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string cliDirectory, string environment, string? centralDbVariable)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(cliDirectory, "Wasla.Cli.dll"));
        start.ArgumentList.Add("list-central-admins"); // reads CentralDb; needs no master key

        // Only what this test sets reaches the child: no developer or runner configuration leaks in.
        foreach (var name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CustomerDb", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OrderHub", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        if (centralDbVariable is not null)
            start.Environment["ConnectionStrings__CentralDb"] = centralDbVariable;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>The CLI's own build output for the configuration this test assembly was built in.</summary>
    private static string CliOutputDirectory()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var targetFramework = testOutput.Name;
        var configuration = testOutput.Parent!.Name;
        var directory = Path.Combine(RepositoryRoot(), "src", "Wasla.Cli", "bin", configuration, targetFramework);
        Assert.True(File.Exists(Path.Combine(directory, "Wasla.Cli.dll")), $"Build Wasla.Cli first: {directory} has no Wasla.Cli.dll.");
        return directory;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Wasla.sln not found above the test output directory.");
    }
}
