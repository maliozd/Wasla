using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Admin;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The existing central activate/deactivate write. Read-only Admin views use
/// <see cref="CentralAdminTenantOperationsService"/>.
/// </summary>
public sealed class CentralAdminTenantService : ICentralAdminTenantService
{
    private readonly CentralDbContext _db;
    private readonly ILogger<CentralAdminTenantService> _logger;

    public CentralAdminTenantService(CentralDbContext db, ILogger<CentralAdminTenantService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> SetTenantActiveStateAsync(Guid tenantId, bool isActive, CancellationToken ct)
    {
        var entity = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            _logger.LogInformation("SetTenantActiveState: tenant {Id} not found", tenantId);
            return false;
        }

        var now = DateTime.UtcNow;
        entity.IsActive = isActive;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
