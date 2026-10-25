using System.Diagnostics;
using System.Security.Cryptography;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// WAS-137: the built CLI, started as a child process, routes the destructive tenant commands through Program.cs to
/// their guards. Production is chosen by the child's own environment, so these tests change nothing in the test
/// process. The child gets a generated fake master key and an unreachable CentralDb; every case is refused before any
/// database is opened.
/// </summary>
public sealed class CliDestructiveCommandProcessTests
{
    private const string Unreachable = "Server=tcp:127.0.0.1,1;Database=WaslaGuardProbe;Connect Timeout=1;ConnectRetryCount=0;Trusted_Connection=True;TrustServerCertificate=True";

    [Theory]
    [InlineData("delete-tenant", "--slug", "demo", "--confirm")]
    [InlineData("delete-tenant", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm")]
    [InlineData("reset-tenant-db", "--slug", "demo", "--confirm")]
    [InlineData("reset-tenant-db", "--tenant-id", "11111111-1111-1111-1111-111111111111", "--confirm")]
    [InlineData("reset-all-tenant-dbs", "--confirm")]
    public async Task InProduction_WithoutForceProduction_TheCommandIsRefused(params string[] args)
    {
        var run = await RunCliAsync("Production", args);

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
        var run = await RunCliAsync("Production", args);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Refusing to run destructive command without --confirm.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("About to", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_IsServedByTheCli_WithTheWaslaHeader()
    {
        var run = await RunCliAsync("Production", "help", "reset-all-tenant-dbs");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("reset-all-tenant-dbs (DESTRUCTIVE)", run.Output, StringComparison.Ordinal);

        var general = await RunCliAsync("Production", "help");

        Assert.Equal(0, general.ExitCode);
        Assert.StartsWith("Wasla CLI", general.Output, StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static async Task<(int ExitCode, string Output)> RunCliAsync(string environment, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(CliOutputDirectory(), "Wasla.Cli.dll"));
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        // Only what this test sets reaches the child: no developer or runner configuration or key leaks in.
        foreach (var name in start.Environment.Keys.ToList())
        {
            if (name.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("TenantDb", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Wasla", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ENCRYPTION_MASTER_KEY", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ConnectionStrings__CentralDb"] = Unreachable;
        start.Environment["ENCRYPTION_MASTER_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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

    /// <summary>The CLI's own build output for the configuration this test assembly was built in. It is only run, never changed.</summary>
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
