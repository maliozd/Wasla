namespace Wasla.Infrastructure.Services;

/// <summary>The SQL Server instance for new tenant databases, or why there is none.</summary>
public sealed record TenantSqlServerResolution(string? Server, string? Error)
{
    public bool IsResolved => Server is not null;
}

/// <summary>
/// Which SQL Server instance provisioning creates tenant databases on (WAS-93). There is no built-in default: an
/// explicit override (CLI <c>--sql-server</c>) wins, then <c>TenantDb:ServerInstance</c>. Unset, empty or
/// whitespace-only values do not count, so with neither provisioning stops before touching any database. LocalDB or
/// <c>.</c> are used only when configured.
/// </summary>
public static class TenantSqlServerConfiguration
{
    public const string ServerInstanceKey = "TenantDb:ServerInstance";
    private const int MaxLength = 256;

    public const string MissingMessage =
        "No SQL Server instance is configured for tenant databases. Set TenantDb:ServerInstance, or pass --sql-server.";

    public static TenantSqlServerResolution Resolve(string? overrideServer, string? configuredServerInstance)
    {
        foreach (var (source, value) in new[]
                 {
                     ("--sql-server", overrideServer),
                     (ServerInstanceKey, configuredServerInstance)
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // The first value that is set decides: an invalid one stops here instead of falling through to the next.
            var server = value.Trim();
            return IsValid(server)
                ? new TenantSqlServerResolution(server, null)
                : new TenantSqlServerResolution(null, $"The SQL Server instance from {source} is not a valid server name.");
        }

        return new TenantSqlServerResolution(null, MissingMessage);
    }

    // A server name, not a connection string: no separators, quotes or control characters.
    private static bool IsValid(string server) =>
        server.Length <= MaxLength
        && !server.Any(ch => char.IsControl(ch) || ch is ';' or '=' or '"' or '\'');
}
