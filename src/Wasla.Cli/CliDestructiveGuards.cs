using Microsoft.Data.SqlClient;
using Wasla.Application.Onboarding;

namespace Wasla.Cli;

/// <summary>
/// Checks that run before a CLI command drops or recreates a tenant database (WAS-93). Each fails closed: missing
/// configuration or an ambiguous target is a refusal, never a default.
/// </summary>
public static class CliDestructiveGuards
{
    /// <summary>
    /// The environment comes from <c>DOTNET_ENVIRONMENT</c>, then <c>ASPNETCORE_ENVIRONMENT</c>. When neither is set the
    /// CLI counts as Production, as the .NET host does, so destructive commands then also need <c>--force-production</c>.
    /// </summary>
    public static bool IsProductionEnvironment(string? dotnetEnvironment, string? aspNetCoreEnvironment)
    {
        var environment = !string.IsNullOrWhiteSpace(dotnetEnvironment) ? dotnetEnvironment
            : !string.IsNullOrWhiteSpace(aspNetCoreEnvironment) ? aspNetCoreEnvironment
            : null;

        return environment is null || string.Equals(environment.Trim(), "Production", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why the database behind a tenant row must not be dropped or recreated, or null when it may.
    /// <paramref name="tenantConnectionString"/> is the decrypted connection string the command would use;
    /// <paramref name="tenantsReferencingDatabase"/> counts the tenant rows that name this database, this one included.
    /// The reason names databases only, never connection-string contents.
    /// </summary>
    public static string? GetDatabaseRefusalReason(
        string tenantDatabaseName,
        string? tenantConnectionString,
        string? centralConnectionString,
        int tenantsReferencingDatabase)
    {
        var centralCatalog = TryGetCatalog(centralConnectionString);
        if (string.IsNullOrWhiteSpace(centralCatalog))
            return "ConnectionStrings:CentralDb is missing or names no database, so the target cannot be checked against CentralDb.";

        var targetCatalog = TryGetCatalog(tenantConnectionString);
        if (string.IsNullOrWhiteSpace(targetCatalog))
            return "The tenant's stored connection string names no database.";

        if (!string.Equals(targetCatalog, tenantDatabaseName?.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"The tenant's connection string targets '{targetCatalog}', not its registered database '{tenantDatabaseName}'.";

        var nameError = TenantDatabaseNames.GetRejectionReason(targetCatalog, centralCatalog);
        if (nameError is not null)
            return nameError;

        if (tenantsReferencingDatabase != 1)
            return $"Database '{targetCatalog}' is referenced by {tenantsReferencingDatabase} tenant rows; it is dropped only when exactly one tenant owns it.";

        return null;
    }

    private static string? TryGetCatalog(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog?.Trim();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
