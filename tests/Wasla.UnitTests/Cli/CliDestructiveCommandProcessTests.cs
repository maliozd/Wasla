namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-137: the built CLI, started as a child process, routes the destructive tenant commands through Program.cs to
/// their guards. Production is chosen by the child's own environment, so these tests change nothing in the test
/// process. The child gets a generated fake master key and an unreachable CentralDb; every case is refused before any
/// database is opened.
/// </summary>
public sealed class CliDestructiveCommandProcessTests
{
    [Theory]
    [InlineData("delete-tenant", "--slug", "demo", "--confirm")]
    [InlineData("delete-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm")]
    [InlineData("reset-tenant-db", "--slug", "demo", "--confirm")]
    [InlineData("reset-tenant-db", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm")]
    [InlineData("reset-all-tenant-dbs", "--confirm")]
    public async Task InProduction_WithoutForceProduction_TheCommandIsRefused(params string[] args)
    {
        var run = await CliProcess.RunAsync("Production", args);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Refusing to run destructive command in Production.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("About to", run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delete-tenant", "--slug", "demo", "--force-production")]
    [InlineData("reset-tenant-db", "--slug", "demo", "--force-production")]
    [InlineData("reset-all-tenant-dbs", "--force-production")]
    public async Task InProduction_ForceProductionWithoutConfirm_IsStillRefused(params string[] args)
    {
        var run = await CliProcess.RunAsync("Production", args);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Refusing to run destructive command without --confirm.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("About to", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_IsServedByTheCli_WithTheWaslaHeader()
    {
        var run = await CliProcess.RunAsync("Production", ["help", "reset-all-tenant-dbs"]);

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("reset-all-tenant-dbs (DESTRUCTIVE)", run.Output, StringComparison.Ordinal);

        var general = await CliProcess.RunAsync("Production", ["help"]);

        Assert.Equal(0, general.ExitCode);
        Assert.StartsWith("Wasla CLI", general.Output, StringComparison.Ordinal);
    }
}
