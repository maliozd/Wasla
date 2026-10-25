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
        // Never adopts a database it did not create for this registration (WAS-93).
        var claim = await SqlServerTenantDatabases
            .ClaimAsync(server, sqlAuth, databaseName, TenantDatabaseOwnership.ForRegistration(registration.Id), ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Tenant database {DatabaseName} for registration {RegistrationId}: {Claim}.",
            databaseName, registration.Id, claim);

        var customerConnString = SqlServerTenantDatabases.BuildConnectionString(server, sqlAuth, databaseName);
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
}
