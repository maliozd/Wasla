namespace Wasla.Application.Abstractions.Admin;

public interface ICentralAdminTenantService
{
    Task<bool> SetCustomerActiveStateAsync(Guid customerId, bool isActive, CancellationToken ct);
}
