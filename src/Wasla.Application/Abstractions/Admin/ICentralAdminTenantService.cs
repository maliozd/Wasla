namespace Wasla.Application.Abstractions.Admin;

public interface ICentralAdminTenantService
{
    Task<bool> SetTenantActiveStateAsync(Guid tenantId, bool isActive, CancellationToken ct);
}
