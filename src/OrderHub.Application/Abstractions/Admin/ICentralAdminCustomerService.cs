namespace OrderHub.Application.Abstractions.Admin;

public interface ICentralAdminCustomerService
{
    Task<CentralAdminDashboardResult> GetDashboardAsync(CancellationToken ct);
    Task<CentralAdminCustomerDetailResult?> GetCustomerAsync(Guid customerId, CancellationToken ct);
    Task<bool> SetCustomerActiveStateAsync(Guid customerId, bool isActive, CancellationToken ct);
}
