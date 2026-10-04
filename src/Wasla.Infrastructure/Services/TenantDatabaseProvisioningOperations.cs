using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Domain.Entities.Central;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed record TenantDatabaseProvisioningWork(
    string ConnectionString,
    DateTime MigratedAtUtc,
    bool OwnerCreated);

public interface ITenantDatabaseProvisioningOperations
{
    Task<TenantDatabaseProvisioningWork> ProvisionAsync(
        PendingRegistration registration,
        string server,
        string sqlAuth,
        string databaseName,
        CancellationToken ct);
}

public sealed class SqlServerTenantDatabaseProvisioningOperations : ITenantDatabaseProvisioningOperations
{
    private static readonly Regex SqlDbNameRegex = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private readonly ILogger<SqlServerTenantDatabaseProvisioningOperations> _logger;

    public SqlServerTenantDatabaseProvisioningOperations(
        ILogger<SqlServerTenantDatabaseProvisioningOperations> logger)
    {
        _logger = logger;
    }

    public async Task<TenantDatabaseProvisioningWork> ProvisionAsync(
        PendingRegistration registration,
        string server,
        string sqlAuth,
        string databaseName,
        CancellationToken ct)
    {
        await EnsureDatabaseExistsAsync(server, sqlAuth, databaseName, ct).ConfigureAwait(false);

        var customerConnString = BuildConnectionString(server, sqlAuth, databaseName);
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(customerConnString)
            .Options;

        var migrationNow = DateTime.UtcNow;
        await using (var tenantDb = new TenantDbContext(tenantOptions))
        {
            await tenantDb.Database.MigrateAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Tenant database {DatabaseName} migrated.", databaseName);

        var ownerCreated = false;
        await using (var tenantDb = new TenantDbContext(tenantOptions))
        {
            var ownerEmail = registration.OwnerEmail.Trim();
            var ownerExists = await tenantDb.AppUsers
                .AnyAsync(u => u.Email == ownerEmail, ct)
                .ConfigureAwait(false);
            if (!ownerExists)
            {
                tenantDb.AppUsers.Add(new AppUser
                {
                    Email = ownerEmail,
                    PasswordHash = registration.PasswordHash,
                    FullName = registration.OwnerFullName.Trim(),
                    Role = UserRole.Owner,
                    IsActive = true,
                    CreatedAt = migrationNow,
                    UpdatedAt = migrationNow
                });
                await tenantDb.SaveChangesAsync(ct).ConfigureAwait(false);
                ownerCreated = true;
            }

            // A new restaurant starts in Setup: orders are kept, but nothing is accepted or printed automatically
            // until a user completes or skips guided setup. A repeated run keeps whatever mode is already stored.
            await TenantOperationalModes.EnsureNewTenantStartsInSetupAsync(tenantDb, migrationNow, ct).ConfigureAwait(false);
        }

        return new TenantDatabaseProvisioningWork(customerConnString, migrationNow, ownerCreated);
    }

    private static async Task EnsureDatabaseExistsAsync(string server, string sqlAuth, string dbName, CancellationToken ct)
    {
        if (!SqlDbNameRegex.IsMatch(dbName))
            throw new InvalidOperationException($"Invalid database name: {dbName}");

        var masterCs = BuildConnectionString(server, sqlAuth, "master");
        await using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string checkSql = "SELECT COUNT(*) FROM sys.databases WHERE name = @name;";
        await using (var check = new SqlCommand(checkSql, conn))
        {
            check.Parameters.AddWithValue("@name", dbName);
            var count = Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)!);
            if (count > 0) return;
        }

        var escaped = dbName.Replace("]", "]]", StringComparison.Ordinal);
        var createSql = $"CREATE DATABASE [{escaped}]";
        await using var create = new SqlCommand(createSql, conn);
        create.CommandTimeout = 120;
        await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildConnectionString(string server, string sqlAuth, string database)
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
