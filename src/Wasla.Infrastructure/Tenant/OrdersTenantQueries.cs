using Wasla.Domain.Enums;
using CentralTenant = Wasla.Domain.Entities.Central.Tenant;

namespace Wasla.Infrastructure.Tenant;

/// <summary>
/// The tenants Wasla Orders works for across the registry (WAS-235): active and assigned to Orders. The Worker's tenant
/// list and the CLI's migration gate and tenant-wide commands use it, so the rule has one definition.
/// </summary>
public static class OrdersTenantQueries
{
    public static IQueryable<CentralTenant> ActiveOrdersTenants(this IQueryable<CentralTenant> tenants) =>
        tenants.Where(t => t.IsActive && t.Product == TenantProduct.Orders);

    /// <summary>Active tenants Orders leaves alone: on POS or between the products. Named in CLI output, never opened.</summary>
    public static IQueryable<CentralTenant> ActiveTenantsNotOnOrders(this IQueryable<CentralTenant> tenants) =>
        tenants.Where(t => t.IsActive && t.Product != TenantProduct.Orders);
}
