using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

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

        // Recent tenants: ordered by creation date descending
        var recent = rows
            .OrderByDescending(c => c.CreatedAt)
            .Take(10)
            .Select(MapListItem)
            .ToList();

        // Pending registration summary counts
        var regGroups = await _db.PendingRegistrations
            .AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var regPendingPayment = regGroups
            .Where(g => g.Status == PendingRegistrationStatus.AwaitingPayment)
            .Sum(g => g.Count);
        var regPaymentReceivedSetupPending = regGroups
            .Where(g => g.Status == PendingRegistrationStatus.PaymentSucceeded)
            .Sum(g => g.Count);
        var regProvisioned = regGroups
            .Where(g => g.Status == PendingRegistrationStatus.Provisioned)
            .Sum(g => g.Count);

        return new CentralAdminDashboardResult
        {
            TotalCustomers = total,
            ActiveCustomers = active,
            InactiveCustomers = inactive,
            Customers = list,
            RecentCustomers = recent,
            RegPendingPayment = regPendingPayment,
            RegPaymentReceivedSetupPending = regPaymentReceivedSetupPending,
            RegProvisioned = regProvisioned
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
