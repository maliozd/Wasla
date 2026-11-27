using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Tenant;

/// <summary>Reads the tenant's product assignment from CentralDb on every call (WAS-235); nothing is cached.</summary>
public sealed class TenantProductReader(CentralDbContext central) : ITenantProductReader
{
    public async Task<bool> IsOrdersTenantAsync(Guid tenantId, CancellationToken ct) =>
        await central.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Product == TenantProduct.Orders, ct)
            .ConfigureAwait(false);
}
