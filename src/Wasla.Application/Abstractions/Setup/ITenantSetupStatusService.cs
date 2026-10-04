namespace Wasla.Application.Abstractions.Setup;

public interface ITenantSetupStatusService
{
    Task<TenantSetupStatus> GetAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task<bool> CompleteGuidanceAsync(Guid tenantId, CancellationToken ct);
}
