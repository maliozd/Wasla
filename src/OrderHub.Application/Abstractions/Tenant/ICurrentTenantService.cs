namespace OrderHub.Application.Abstractions.Tenant;

public interface ICurrentTenantService
{
    ResolvedTenantDto? CurrentTenant { get; }
}
