namespace OrderHub.Application.Abstractions.Tenant;

public interface ICustomerResolver
{
    Task<ResolvedCustomerDto?> ResolveByHostAsync(string host, CancellationToken ct);
}

