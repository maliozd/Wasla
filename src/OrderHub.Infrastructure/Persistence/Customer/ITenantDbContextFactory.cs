namespace OrderHub.Infrastructure.Persistence.Customer;

public interface ITenantDbContextFactory
{
    Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct);
}

