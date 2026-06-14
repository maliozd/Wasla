namespace OrderHub.Application.Abstractions.Admin;

public interface ICentralAdminTenantService
{
    Task<CentralAdminDashboardResult> GetDashboardAsync(CancellationToken ct);
    Task<CentralAdminTenantDetailResult?> GetCustomerAsync(Guid customerId, CancellationToken ct);
    Task<bool> SetCustomerActiveStateAsync(Guid customerId, bool isActive, CancellationToken ct);
}
