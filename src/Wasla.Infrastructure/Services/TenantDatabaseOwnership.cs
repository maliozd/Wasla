using Microsoft.Data.SqlClient;
using Wasla.Application.Onboarding;

namespace Wasla.Infrastructure.Services;

/// <summary>What provisioning may do with a tenant database name on the SQL Server.</summary>
public enum TenantDatabaseClaim
{
    /// <summary>No database has that name; it is created and marked.</summary>
    Create,

    /// <summary>The database exists and carries this registration's (or tenant's) marker: an earlier attempt created it.</summary>
    Resume
}

/// <summary>
/// Raised when a tenant database name is taken on the SQL Server by a database Wasla cannot prove it created for the
/// same registration or tenant. Provisioning never adopts such a database (WAS-93).
/// </summary>
public sealed class TenantDatabaseOwnershipException : InvalidOperationException
{
    public TenantDatabaseOwnershipException(string databaseName)
        : base($"Database '{databaseName}' already exists and is not marked as created by Wasla for this registration. " +
               "Provisioning will not adopt it; an operator must inspect that database.")
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }
}

/// <summary>
/// Ownership marker for tenant databases (WAS-93). Right after <c>CREATE DATABASE</c>, provisioning stores a
/// database-level extended property naming the registration (signup) or tenant (CLI <c>add-tenant</c>) it created
/// the database for. A database that already exists is only used again when that marker names the same registration
/// or tenant; otherwise provisioning stops. Tenant databases created before WAS-93 have no marker, which matters only
/// if one of them were provisioned again: that is refused.
/// </summary>
public static class TenantDatabaseOwnership
{
    public const string PropertyName = "Wasla.TenantDatabaseOwner";

    public static string ForRegistration(Guid registrationId) => $"PendingRegistration:{registrationId:D}";

    public static string ForTenant(Guid tenantId) => $"Tenant:{tenantId:D}";

    /// <summary>
    /// Decides from what the server reports. Only an exact marker match resumes; a missing, different or unreadable
    /// marker throws <see cref="TenantDatabaseOwnershipException"/>.
    /// </summary>
    public static TenantDatabaseClaim Evaluate(string databaseName, bool exists, string? marker, string expectedMarker)
    {
        if (string.IsNullOrWhiteSpace(expectedMarker))
            throw new ArgumentException("An expected ownership marker is required.", nameof(expectedMarker));

        if (!exists)
            return TenantDatabaseClaim.Create;

        if (marker is not null && string.Equals(marker, expectedMarker, StringComparison.Ordinal))
            return TenantDatabaseClaim.Resume;

        throw new TenantDatabaseOwnershipException(databaseName);
    }
}

/// <summary>SQL Server side of <see cref="TenantDatabaseOwnership"/>: creates a tenant database with its marker, or verifies one.</summary>
public static class SqlServerTenantDatabases
{
    /// <summary>
    /// Creates <paramref name="databaseName"/> and marks it with <paramref name="expectedMarker"/>, or, when it exists,
    /// returns <see cref="TenantDatabaseClaim.Resume"/> only if it already carries that marker. System database names are
    /// refused here as well. <c>CREATE DATABASE</c> cannot run in a transaction: if the process stops between creating and
    /// marking, the next attempt finds an unmarked database and refuses it, and an operator removes it.
    /// </summary>
    public static async Task<TenantDatabaseClaim> ClaimAsync(
        string server,
        string sqlAuth,
        string databaseName,
        string expectedMarker,
        CancellationToken ct)
    {
        if (TenantDatabaseNames.IsSystemDatabaseName(databaseName)
            || TenantDatabaseNames.GetRejectionReason(databaseName, centralCatalog: "\u0000not-checked-here") is not null)
            throw new InvalidOperationException($"Invalid tenant database name: {databaseName}");

        bool exists;
        await using (var master = new SqlConnection(BuildConnectionString(server, sqlAuth, "master")))
        {
            await master.OpenAsync(ct).ConfigureAwait(false);
            await using var check = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @name;", master);
            check.Parameters.AddWithValue("@name", databaseName);
            exists = Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;

            if (!exists)
            {
                var escaped = databaseName.Replace("]", "]]", StringComparison.Ordinal);
                await using var create = new SqlCommand($"CREATE DATABASE [{escaped}]", master) { CommandTimeout = 120 };
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await using var tenant = new SqlConnection(BuildConnectionString(server, sqlAuth, databaseName));
        await tenant.OpenAsync(ct).ConfigureAwait(false);

        if (!exists)
        {
            await using var mark = new SqlCommand("EXEC sys.sp_addextendedproperty @name = @name, @value = @value;", tenant);
            mark.Parameters.AddWithValue("@name", TenantDatabaseOwnership.PropertyName);
            mark.Parameters.AddWithValue("@value", expectedMarker);
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return TenantDatabaseClaim.Create;
        }

        await using var read = new SqlCommand(
            "SELECT CAST(value AS nvarchar(4000)) FROM sys.extended_properties WHERE class = 0 AND name = @name;",
            tenant);
        read.Parameters.AddWithValue("@name", TenantDatabaseOwnership.PropertyName);
        var marker = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return TenantDatabaseOwnership.Evaluate(databaseName, exists: true, marker, expectedMarker);
    }

    public static string BuildConnectionString(string server, string sqlAuth, string database)
    {
        if (string.Equals(sqlAuth, "trusted", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;
        }

        if (sqlAuth.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = sqlAuth[4..];
            var idx = rest.IndexOf(':');
            if (idx <= 0)
                throw new InvalidOperationException("Invalid sqlAuth format. Use 'trusted' or 'sql:username:password'.");
            return new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                UserID = rest[..idx],
                Password = rest[(idx + 1)..],
                TrustServerCertificate = true
            }.ConnectionString;
        }

        throw new InvalidOperationException("Invalid sqlAuth. Use 'trusted' or 'sql:username:password'.");
    }
}
