using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.Tenant;

public sealed class CustomerResolver : ICustomerResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly CentralDbContext _centralDb;

    public CustomerResolver(IMemoryCache cache, CentralDbContext centralDb)
    {
        _cache = cache;
        _centralDb = centralDb;
    }

    public async Task<ResolvedCustomerDto?> ResolveByHostAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var normalized = host.Trim().ToLowerInvariant();
        var cacheKey = $"customer:{normalized}";

        if (_cache.TryGetValue(cacheKey, out ResolvedCustomerDto? cached) && cached is not null)
            return cached;

        var customer = await _centralDb.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PrimaryDomain.ToLower() == normalized, ct);

        if (customer is null) return null;
        if (!customer.IsActive) return null;

        var dto = new ResolvedCustomerDto(customer.Id, customer.Name, customer.Slug, customer.PrimaryDomain);
        _cache.Set(cacheKey, dto, CacheTtl);
        return dto;
    }
}

