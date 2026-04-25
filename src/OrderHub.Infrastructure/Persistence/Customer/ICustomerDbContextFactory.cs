namespace OrderHub.Infrastructure.Persistence.Customer;

public interface ICustomerDbContextFactory
{
    Task<CustomerDbContext> CreateAsync(Guid customerId, CancellationToken ct);
}

