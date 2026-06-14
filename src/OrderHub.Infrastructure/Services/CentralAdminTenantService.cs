using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Admin;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.Services;

public sealed class CentralAdminTenantService : ICentralAdminTenantService
{
    private readonly CentralDbContext _db;
    private readonly ILogger<CentralAdminTenantService> _logger;

    public CentralAdminTenantService(CentralDbContext db, ILogger<CentralAdminTenantService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<CentralAdminDashboardResult> GetDashboardAsync(CancellationToken ct)
    {
        var rows = await _db.Tenants
            .AsNoTracking()
            .OrderBy(c => c.Slug)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = rows.Count;
        var active = rows.Count(c => c.IsActive);
        var inactive = total - active;

        var list = rows.Select(MapListItem).ToList();

        return new CentralAdminDashboardResult
        {
            TotalCustomers = total,
            ActiveCustomers = active,
            InactiveCustomers = inactive,
            Customers = list
        };
    }

    public async Task<CentralAdminTenantDetailResult?> GetCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var c = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == customerId, ct)
            .ConfigureAwait(false);

        return c is null ? null : MapDetail(c);
    }

    public async Task<bool> SetCustomerActiveStateAsync(Guid customerId, bool isActive, CancellationToken ct)
    {
        var entity = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == customerId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            _logger.LogInformation("SetCustomerActiveState: tenant {Id} not found", customerId);
            return false;
        }

        var now = DateTime.UtcNow;
        entity.IsActive = isActive;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static CentralAdminTenantListItemDto MapListItem(Domain.Entities.Central.Tenant c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Slug = c.Slug,
        PrimaryDomain = c.PrimaryDomain,
        DatabaseName = c.DatabaseName,
        IsActive = c.IsActive,
        SchemaVersion = c.SchemaVersion,
        LastMigrationAt = c.LastMigrationAt,
        LastMigrationResult = c.LastMigrationResult,
        CreatedAt = c.CreatedAt
    };

    private static CentralAdminTenantDetailResult MapDetail(Domain.Entities.Central.Tenant c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Slug = c.Slug,
        PrimaryDomain = c.PrimaryDomain,
        DatabaseName = c.DatabaseName,
        IsActive = c.IsActive,
        SchemaVersion = c.SchemaVersion,
        LastMigrationAt = c.LastMigrationAt,
        LastMigrationResult = c.LastMigrationResult,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };
}
