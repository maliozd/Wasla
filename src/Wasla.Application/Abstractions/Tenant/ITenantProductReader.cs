namespace Wasla.Application.Abstractions.Tenant;

/// <summary>
/// Reads a tenant's product assignment (WAS-235) where Wasla Orders is about to work for the tenant: the Worker at each
/// tenant and connection pass, order actions, platform connection changes and the webhook processor. Every call reads
/// CentralDb again and caches nothing, so an assignment change is seen at the next boundary. It narrows the window in
/// which Orders still works for a tenant that has left it; it does not stop work already in flight (WAS-240).
/// </summary>
public interface ITenantProductReader
{
    /// <summary>
    /// Whether Wasla Orders serves the tenant now: it exists and is assigned to Orders. A tenant that is missing, on
    /// POS or between the two products is not served. Whether it is active is checked where it always was.
    /// </summary>
    Task<bool> IsOrdersTenantAsync(Guid tenantId, CancellationToken ct);
}
