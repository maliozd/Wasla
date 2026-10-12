using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Wasla.Cli;

/// <summary>
/// The CLI's configuration sources, lowest to highest precedence (WAS-93):
/// <c>appsettings.json</c>, then <c>appsettings.{Environment}.json</c> (both next to the CLI binary, so the working
/// directory does not matter), user secrets in Development, environment variables, and the command line. A deployment's
/// environment variables therefore always win over the committed JSON. The committed base <c>appsettings.json</c> names
/// no CentralDb and no tenant SQL Server; Development sets both in <c>appsettings.Development.json</c>.
/// </summary>
public static class CliConfiguration
{
    /// <param name="configuration">Its existing sources are replaced.</param>
    /// <param name="userSecretsAssembly">The assembly whose user secrets Development reads; null reads none.</param>
    /// <param name="environmentVariablePrefix">
    /// Null reads every environment variable, as the CLI does. Tests pass a unique prefix so they never set
    /// process-wide variables another test could see.
    /// </param>
    public static void Configure(
        IConfigurationBuilder configuration,
        string baseDirectory,
        string environmentName,
        string[] args,
        Assembly? userSecretsAssembly = null,
        string? environmentVariablePrefix = null)
    {
        configuration.Sources.Clear();
        configuration.AddJsonFile(Path.Combine(baseDirectory, "appsettings.json"), optional: false, reloadOnChange: false);
        configuration.AddJsonFile(Path.Combine(baseDirectory, $"appsettings.{environmentName}.json"), optional: true, reloadOnChange: false);

        if (userSecretsAssembly is not null && string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            configuration.AddUserSecrets(userSecretsAssembly, optional: true, reloadOnChange: false);

        if (environmentVariablePrefix is null)
            configuration.AddEnvironmentVariables();
        else
            configuration.AddEnvironmentVariables(environmentVariablePrefix);

        configuration.AddCommandLine(args);
    }
}
