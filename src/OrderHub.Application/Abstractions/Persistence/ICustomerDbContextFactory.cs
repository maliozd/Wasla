using Microsoft.EntityFrameworkCore;

namespace OrderHub.Application.Abstractions.Persistence;

public interface ICustomerDbContextFactory
{
    Task<DbContext> CreateAsync(Guid customerId, CancellationToken ct);
}

