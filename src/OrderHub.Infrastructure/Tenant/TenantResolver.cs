using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.Tenant;

public sealed class TenantResolver : ITenantResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly CentralDbContext _centralDb;

    public TenantResolver(IMemoryCache cache, CentralDbContext centralDb)
    {
        _cache = cache;
        _centralDb = centralDb;
    }

    public async Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var normalized = host.Trim().ToLowerInvariant();
        var cacheKey = $"customer:{normalized}";

        if (_cache.TryGetValue(cacheKey, out ResolvedTenantDto? cached) && cached is not null)
            return cached;

        var tenant = await _centralDb.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PrimaryDomain.ToLower() == normalized, ct);

        if (tenant is null) return null;
        if (!tenant.IsActive) return null;

        var dto = new ResolvedTenantDto(tenant.Id, tenant.Name, tenant.Slug, tenant.PrimaryDomain);
        _cache.Set(cacheKey, dto, CacheTtl);
        return dto;
    }
}
