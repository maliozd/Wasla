namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-42: the built CLI, started as a child process, lets <c>create-user</c> through the known-command gate in
/// <c>Program.cs</c> to the parser and its handler, still requires the master key for it, and serves its help. CentralDb
/// is an unreachable local port, so no database is opened and no user can be created.
/// </summary>
public sealed class CliCreateUserProcessTests
{
    private const string FakePassword = "Fake-password-1";

    [Fact]
    public async Task CreateUser_PassesTheEntryPointGate_AndReachesItsHandler()
    {
        var run = await CliProcess.RunAsync("Production", ["create-user", "--tenant-slug", "demo", "--email", "kitchen@example.com", "--password", FakePassword, "--role", "Owner,Manager"]);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Role must be one of: Owner, Manager, Kitchen, Cashier, Viewer.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_WithAMissingOption_IsRefusedByTheParser()
    {
        var run = await CliProcess.RunAsync("Production", ["create-user", "--tenant-slug", "demo", "--email", "kitchen@example.com", "--password", FakePassword]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Option '--role' is required.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_WithValidInput_FailsOnTheUnreachableCentralDb_WithoutPrintingThePassword()
    {
        var run = await CliProcess.RunAsync("Production", ["create-user", "--tenant-slug", "demo", "--email", "kitchen@example.com", "--password", FakePassword, "--role", "Kitchen"]);

        // 3 for the SqlException; 1 should EF Core report the failure as transient instead.
        Assert.Contains(run.ExitCode, new[] { 3, 1 });
        Assert.Contains("No user was created.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUser_WithoutTheMasterKey_IsRefusedBeforeTheHostIsBuilt()
    {
        var run = await CliProcess.RunAsync("Production", ["create-user", "--tenant-slug", "demo", "--email", "kitchen@example.com", "--password", FakePassword, "--role", "Kitchen"], withMasterKey: false);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Missing required environment variable 'ENCRYPTION_MASTER_KEY'.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("No user was created.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePassword, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpCreateUser_IsServed_AndTheGeneralHelpListsIt()
    {
        var run = await CliProcess.RunAsync("Production", ["help", "create-user"], withMasterKey: false);

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("create-user" + Environment.NewLine, run.Output, StringComparison.Ordinal);
        Assert.Contains("create-user --tenant-slug <slug> --email <email> --password <password> --role <role> [--name <full name>]", run.Output, StringComparison.Ordinal);

        var general = await CliProcess.RunAsync("Production", ["help"], withMasterKey: false);

        Assert.Equal(0, general.ExitCode);
        Assert.Contains("  create-user               Add a user with a role to an existing active tenant.", general.Output, StringComparison.Ordinal);
    }
}
