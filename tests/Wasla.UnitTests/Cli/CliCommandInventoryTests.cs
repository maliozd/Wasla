using System.CommandLine;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Application.Abstractions.Security;
using Wasla.Cli;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-137: the CLI registers, gates and documents exactly the approved command tree. Every command and option has one
/// name and no alias; <see cref="CliHelpPrinter"/> lets through and lists the same commands; and each tenant command and
/// option reaches its handler. Handlers run against an empty in-memory CentralDb and stop on validation, so no tenant
/// database is opened, created or dropped.
/// </summary>
[Collection(nameof(CliConsoleCollection))]
public sealed class CliCommandInventoryTests
{
    /// <summary>The approved tree: every registered command with its exact options.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> ApprovedTree = new Dictionary<string, string[]>
    {
        ["add-tenant"] = ["--name", "--slug", "--domain", "--admin-email", "--admin-password", "--admin-name", "--sql-server", "--sql-auth", "--business-phone", "--city", "--country"],
        ["provision-signup-request"] = ["--registration-id", "--dry-run", "--force", "--sql-server", "--sql-auth"],
        ["add-central-admin"] = ["--email", "--password", "--display-name"],
        ["reset-central-admin-password"] = ["--email", "--password"],
        ["reset-password"] = ["--scope", "--email", "--tenant", "--dry-run"],
        ["list-central-admins"] = [],
        ["migrate-central"] = [],
        ["seed-turkey-reference-data"] = [],
        ["seed-address-reference-data"] = [],
        ["migrate-tenant"] = ["--slug", "--tenant-id"],
        ["migrate-all-tenants"] = ["--dry-run", "--only"],
        ["migration-status"] = ["--require-current"],
        ["delete-tenant"] = ["--slug", "--tenant-id", "--confirm", "--force-production"],
        ["reset-tenant-db"] = ["--slug", "--tenant-id", "--confirm", "--force-production"],
        ["reset-all-tenant-dbs"] = ["--confirm", "--force-production"],
        ["seed-tenant-admin"] = ["--slug", "--admin-email", "--admin-password", "--admin-name"],
        ["update-tenant-profile"] = ["--tenant", "--business-phone", "--city", "--country"],
        ["list-tenants"] = [],
        ["encrypt"] = [],
        ["create-user"] = ["--tenant-slug", "--email", "--password", "--role", "--name"],
        ["generate-print-bridge-token"] = ["--tenant-id", "--slug", "--name"],
        ["seed-print-job"] = ["--slug", "--tenant-id"],
        ["list-print-jobs"] = ["--slug", "--tenant-id"],
        ["meal-webhook-integrator"] = ["--action", "--name", "--base-url", "--token-file"],
        ["meal-webhook-inbox"] = ["--limit", "--replay"],
    };

    /// <summary>Handled in Program.cs before the command tree is built, so they are known but not registered.</summary>
    private static readonly string[] HandledBeforeTheTree = ["help", "hash-password"];

    /// <summary>
    /// Registered, but missing from the help printer's known-command gate, so Program.cs rejects it as unknown before
    /// the tree runs (documented in docs/operations/cli.md). Kept as is; this test fails when either side changes.
    /// </summary>
    private const string GatedOutCommand = "create-user";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Registered tree ----------------------------------------------------------------------------

    [Fact]
    public void TheRootCommand_RegistersExactlyTheApprovedCommands()
    {
        var root = CliCommandTree.Build(EmptyHost());

        Assert.Equal(
            ApprovedTree.Keys.Order(StringComparer.Ordinal),
            root.Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Empty(root.Options);
        Assert.Empty(root.Arguments);
    }

    [Fact]
    public void EveryCommand_HasExactlyTheApprovedOptions_AndNoNestedCommands()
    {
        var root = CliCommandTree.Build(EmptyHost());

        foreach (var command in root.Subcommands)
        {
            Assert.Equal(
                ApprovedTree[command.Name].Order(StringComparer.Ordinal),
                command.Options.Select(o => Assert.Single(o.Aliases)).Order(StringComparer.Ordinal));
            Assert.Empty(command.Subcommands);
        }
    }

    [Fact]
    public void NoCommandOrOption_HasAnAlias()
    {
        var root = CliCommandTree.Build(EmptyHost());

        Assert.Equal([root.Name], root.Aliases);
        foreach (var command in root.Subcommands)
        {
            Assert.Equal([command.Name], command.Aliases);
            foreach (var option in command.Options)
                Assert.Equal(["--" + option.Name], option.Aliases);
        }
    }

    [Fact]
    public void OnlyEncrypt_TakesAnArgument()
    {
        var root = CliCommandTree.Build(EmptyHost());

        var withArguments = root.Subcommands.Where(c => c.Arguments.Count > 0).ToList();

        var encrypt = Assert.Single(withArguments);
        Assert.Equal("encrypt", encrypt.Name);
        Assert.Equal("plaintext", Assert.Single(encrypt.Arguments).Name);
    }

    [Fact]
    public void TheRootDescription_NamesWasla()
    {
        var root = CliCommandTree.Build(EmptyHost());

        Assert.StartsWith("Wasla CLI", root.Description, StringComparison.Ordinal);
    }

    // --- Known-command gate -------------------------------------------------------------------------

    [Fact]
    public void TheKnownCommandGate_MatchesTheRegisteredCommands_ExceptTheDocumentedCreateUserGap()
    {
        var registered = CliCommandTree.Build(EmptyHost()).Subcommands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var expected = registered
            .Where(name => name != GatedOutCommand)
            .Concat(HandledBeforeTheTree)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, CliHelpPrinter.KnownCommands.Order(StringComparer.Ordinal));
        Assert.Contains(GatedOutCommand, registered);
        Assert.False(CliHelpPrinter.IsKnownCommand(GatedOutCommand));
    }

    // --- Help text ----------------------------------------------------------------------------------

    [Fact]
    public async Task GeneralHelp_HasTheWaslaHeader_AndListsExactlyTheKnownCommands()
    {
        var output = await CaptureAsync(() => { CliHelpPrinter.PrintGeneralHelp(); return Task.FromResult(0); });

        var lines = output.Output.Split(Environment.NewLine);
        Assert.Equal("Wasla CLI", lines[0]);
        Assert.Contains("Tenant commands:", lines);

        Assert.Equal(
            CliHelpPrinter.KnownCommands.Order(StringComparer.Ordinal),
            ListedCommands(lines).Order(StringComparer.Ordinal));
        AssertMentionsOnlyKnownCommandsAndRegisteredOptions(output.Output);
    }

    [Fact]
    public async Task CommandHelp_StartsWithTheCommand_AndMentionsOnlyKnownCommandsAndRegisteredOptions()
    {
        foreach (var command in CliHelpPrinter.KnownCommands)
        {
            var output = await CaptureAsync(() => { CliHelpPrinter.PrintCommandHelp(command); return Task.FromResult(0); });

            Assert.StartsWith(command, output.Output, StringComparison.Ordinal);
            AssertMentionsOnlyKnownCommandsAndRegisteredOptions(output.Output);
        }
    }

    [Theory]
    [InlineData("add-tenant", "add-tenant --name <name> --slug <slug>")]
    [InlineData("update-tenant-profile", "update-tenant-profile --tenant <slug-or-id>")]
    [InlineData("delete-tenant", "delete-tenant --tenant-id <guid> --confirm")]
    [InlineData("reset-tenant-db", "reset-tenant-db --tenant-id <guid> --confirm")]
    [InlineData("reset-all-tenant-dbs", "reset-all-tenant-dbs --confirm")]
    [InlineData("migrate-all-tenants", "-- migrate-all-tenants")]
    public async Task CommandHelp_ShowsTheTenantCommandUsage(string command, string expected)
    {
        var output = await CaptureAsync(() => { CliHelpPrinter.PrintCommandHelp(command); return Task.FromResult(0); });

        Assert.Contains(expected, output.Output, StringComparison.Ordinal);
    }

    // --- Parser routing -----------------------------------------------------------------------------

    public static TheoryData<string[], string, string, string> TenantCommandLines => new()
    {
        { ["migrate-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111"], "migrate-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["migrate-tenant", "--slug", "demo"], "migrate-tenant", "--slug", "demo" },
        { ["migrate-all-tenants", "--only", "demo", "--only", "other"], "migrate-all-tenants", "--only", "demo,other" },
        { ["delete-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm"], "delete-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["reset-tenant-db", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm", "--force-production"], "reset-tenant-db", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["reset-all-tenant-dbs", "--confirm"], "reset-all-tenant-dbs", "--confirm", "True" },
        { ["add-tenant", "--name", "Demo", "--slug", "demo", "--domain", "demo.wasla.local", "--admin-email", "owner@demo.test", "--admin-password", "Fake-password-1"], "add-tenant", "--slug", "demo" },
        { ["list-tenants"], "list-tenants", "", "" },
        { ["seed-tenant-admin", "--slug", "demo", "--admin-email", "owner@demo.test", "--admin-password", "Fake-password-1"], "seed-tenant-admin", "--slug", "demo" },
        { ["update-tenant-profile", "--tenant", "demo", "--city", "Istanbul"], "update-tenant-profile", "--tenant", "demo" },
        { ["generate-print-bridge-token", "--tenant-id", "11111111-1111-1111-1111-111111111111"], "generate-print-bridge-token", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["seed-print-job", "--tenant-id", "11111111-1111-1111-1111-111111111111"], "seed-print-job", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["list-print-jobs", "--tenant-id", "11111111-1111-1111-1111-111111111111"], "list-print-jobs", "--tenant-id", "11111111-1111-1111-1111-111111111111" },
        { ["create-user", "--tenant-slug", "demo", "--email", "user@demo.test", "--password", "Fake-password-1", "--role", "Viewer"], "create-user", "--tenant-slug", "demo" },
    };

    [Theory]
    [MemberData(nameof(TenantCommandLines))]
    public void TenantCommandLines_ParseToTheirCommand_WithTheTenantOptionValue(string[] args, string command, string option, string value)
    {
        var root = CliCommandTree.Build(EmptyHost());

        var result = root.Parse(args);

        Assert.Empty(result.Errors);
        Assert.Equal(command, result.CommandResult.Command.Name);
        if (option.Length == 0)
            return;

        var parsed = result.GetValueForOption(Assert.Single(result.CommandResult.Command.Options, o => o.Aliases.Contains(option)));
        Assert.Equal(value, parsed is string[] values ? string.Join(",", values) : Convert.ToString(parsed));
    }

    public static TheoryData<string[], int, string> TenantCommandHandlers => new()
    {
        { ["migrate-tenant"], 2, "Specify --slug or --tenant-id." },
        { ["migrate-tenant", "--tenant-id", "not-a-guid"], 2, "--tenant-id must be a valid GUID." },
        { ["migrate-tenant", "--slug", "demo", "--tenant-id", "11111111-1111-1111-1111-111111111111"], 2, "Use either --slug or --tenant-id, not both." },
        { ["migrate-all-tenants", "--only", "unknown-tenant"], 2, "Tenant slug not found: unknown-tenant" },
        { ["list-tenants"], 0, "| Slug" },
        { ["add-tenant", "--name", "Demo", "--slug", "not a slug", "--domain", "demo.wasla.local", "--admin-email", "owner@demo.test", "--admin-password", "Fake-password-1"], 2, "Slug must match" },
        { ["seed-tenant-admin", "--slug", "not a slug", "--admin-email", "owner@demo.test", "--admin-password", "Fake-password-1"], 2, "Slug must match" },
        { ["update-tenant-profile", "--tenant", "unknown-tenant", "--city", "Istanbul"], 2, "Tenant not found." },
        { ["generate-print-bridge-token", "--tenant-id", "11111111-1111-1111-1111-111111111111"], 2, "Tenant not found or inactive." },
        { ["generate-print-bridge-token"], 2, "Usage: generate-print-bridge-token --tenant-id <guid> | --slug <slug>" },
        { ["seed-print-job", "--tenant-id", "not-a-guid"], 2, "--tenant-id must be a valid GUID." },
        { ["list-print-jobs"], 2, "Specify --slug or --tenant-id." },
        { ["create-user", "--tenant-slug", "not a slug", "--email", "user@demo.test", "--password", "Fake-password-1", "--role", "Viewer"], 2, "Tenant slug must match" },
    };

    [Theory]
    [MemberData(nameof(TenantCommandHandlers))]
    public async Task TenantCommands_ReachTheirHandlerValidation(string[] args, int expectedExitCode, string expectedOutput)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        using var host = CentralOnlyHost(connection);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CentralDbContext>().Database.EnsureCreatedAsync(Ct);

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(args));

        Assert.Equal(expectedExitCode, code);
        Assert.Contains(expectedOutput, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrationStatus_RequireCurrent_RejectsPendingCentralMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        using var host = CentralOnlyHost(connection);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CentralDbContext>().Database.EnsureCreatedAsync(Ct);

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["migration-status", "--require-current"]));

        Assert.Equal(1, code);
        Assert.Contains("Migration readiness check failed.", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    public async Task MigrationStatus_RequireCurrent_RejectsUnknownHistoryAndUnreadableActiveTenant(
        bool unknownHistory, bool unreadableTenant, int expectedCode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        using var host = CentralOnlyHost(connection);
        using (var scope = host.Services.CreateScope())
        {
            var central = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            await central.Database.EnsureCreatedAsync(Ct);
            await central.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);");
            foreach (var migration in central.Database.GetMigrations())
                await central.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"__EFMigrationsHistory\" VALUES ({0}, '8.0.10');", migration);
            if (unknownHistory)
                await central.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20990101_NewerBuild', '8.0.10');");
            if (unreadableTenant)
            {
                central.Tenants.Add(new Tenant
                {
                    Name = "Unreachable", Slug = "unreachable", PrimaryDomain = "unreachable.app.wasla.local",
                    DatabaseName = "Wasla_Unreachable", EncryptedConnectionString = "fake"
                });
                await central.SaveChangesAsync(Ct);
            }
        }

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(["migration-status", "--require-current"]));

        Assert.Equal(expectedCode, code);
        Assert.Contains(expectedCode == 0 ? "All active databases are current" : "Migration readiness check failed", output, StringComparison.Ordinal);
        if (unreadableTenant)
            Assert.Contains("(could not read migration status) InvalidOperationException", output, StringComparison.Ordinal);
    }

    // --- Destructive guards -------------------------------------------------------------------------

    [Theory]
    [InlineData("delete-tenant", "--slug", "demo")]
    [InlineData("delete-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111")]
    [InlineData("reset-tenant-db", "--slug", "demo")]
    [InlineData("reset-tenant-db", "--tenant-id", "11111111-1111-1111-1111-111111111111")]
    [InlineData("reset-all-tenant-dbs")]
    [InlineData("reset-all-tenant-dbs", "--force-production")]
    public async Task DestructiveTenantCommands_WithoutConfirm_AreRefusedBeforeAnyDatabaseAccess(params string[] args)
    {
        // A host without services: any database access would throw instead of returning the refusal.
        using var host = EmptyHost();

        var (code, output) = await CaptureAsync(() => CliCommandTree.Build(host).InvokeAsync(args));

        Assert.Equal(2, code);
        Assert.Contains("Refusing to run destructive command without --confirm.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("About to", output, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static IEnumerable<string> ListedCommands(IEnumerable<string> lines)
    {
        var inSection = false;
        foreach (var line in lines)
        {
            if (line.EndsWith(" commands:", StringComparison.Ordinal))
            {
                inSection = true;
                continue;
            }

            if (line == "Examples:")
                yield break;

            if (inSection && line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ')
                yield return line[2..].Split(' ')[0];
        }
    }

    private static void AssertMentionsOnlyKnownCommandsAndRegisteredOptions(string output)
    {
        var registeredOptions = CliCommandTree.Build(EmptyHost()).Subcommands
            .SelectMany(c => c.Options)
            .SelectMany(o => o.Aliases)
            .Append("--help")
            .Append("--project") // dotnet run's own option in the example lines
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match option in Regex.Matches(output, @"(?<![\w-])--[a-z][a-z-]*"))
            Assert.Contains(option.Value, registeredOptions);

        // Every "-- <command>" in an example or usage line names a known command.
        foreach (Match invocation in Regex.Matches(output, @"\s--\s([a-z][a-z-]*)"))
            Assert.Contains(invocation.Groups[1].Value, CliHelpPrinter.KnownCommands);
    }

    private static IHost EmptyHost() => new HostBuilder().Build();

    private static IHost CentralOnlyHost(SqliteConnection connection) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContext<CentralDbContext>(options => options.UseSqlite(connection));
                services.AddSingleton<ISecretManager, UnusedSecretManager>();
            })
            .Build();

    private static async Task<(int Code, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            var code = await action();
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class UnusedSecretManager : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            throw new InvalidOperationException("No secret may be encrypted by these tests.");

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            throw new InvalidOperationException("No tenant connection string may be decrypted by these tests.");
    }
}
