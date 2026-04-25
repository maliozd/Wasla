namespace OrderHub.Application.Abstractions.Tenant;

public interface ICurrentCustomerService
{
    ResolvedCustomerDto? CurrentCustomer { get; }
}

