namespace Wasla.Application.Abstractions.Tenant;

public interface ITenantResolver
{
    Task<ResolvedTenantDto?> ResolveByHostAsync(string host, CancellationToken ct);
}
