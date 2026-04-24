using OrderHub.Domain.Entities.Central;

namespace OrderHub.Application.Abstractions.Tenant;

public interface ICurrentCustomerService
{
    Customer? CurrentCustomer { get; }
}

