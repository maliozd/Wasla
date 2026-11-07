using System.Diagnostics;
using System.Security.Cryptography;

namespace Wasla.UnitTests.Cli;

/// <summary>
/// Runs the built CLI as a child process, through <c>Program.cs</c>, with only the environment given here: no developer
/// or runner configuration or key reaches it. CentralDb is an unreachable local port, so no database is ever opened, and
/// the master key, when given, is generated for the run. The child reads its own console, so callers need no
/// <see cref="CliConsoleCollection"/>.
/// </summary>
internal static class CliProcess
{
    private const string UnreachableCentralDb = "Server=tcp:127.0.0.1,1;Database=WaslaCliProbe;Connect Timeout=1;ConnectRetryCount=0;Trusted_Connection=True;TrustServerCertificate=True";

    public static async Task<(int ExitCode, string Output)> RunAsync(string environment, string[] args, bool withMasterKey = true)
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
        start.Environment["ConnectionStrings__CentralDb"] = UnreachableCentralDb;
        if (withMasterKey)
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
