namespace Wasla.Infrastructure.Persistence.Tenant;

public interface ITenantDbContextFactory
{
    Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct);
}

