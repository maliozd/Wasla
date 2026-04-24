using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Persistence;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Infrastructure.Persistence.Central;

namespace OrderHub.Infrastructure.Persistence.Customer;

public sealed class CustomerDbContextFactory : ICustomerDbContextFactory
{
    private readonly CentralDbContext _centralDb;
    private readonly ISecretManager _secretManager;

    private readonly ConcurrentDictionary<Guid, Lazy<Task<DbContextOptions<CustomerDbContext>>>> _optionsCache = new();

    public CustomerDbContextFactory(CentralDbContext centralDb, ISecretManager secretManager)
    {
        _centralDb = centralDb;
        _secretManager = secretManager;
    }

    public async Task<DbContext> CreateAsync(Guid customerId, CancellationToken ct)
    {
        var optionsLazy = _optionsCache.GetOrAdd(
            customerId,
            id => new Lazy<Task<DbContextOptions<CustomerDbContext>>>(() => BuildOptionsAsync(id, ct)));

        var options = await optionsLazy.Value.ConfigureAwait(false);
        return new CustomerDbContext(options);
    }

    private async Task<DbContextOptions<CustomerDbContext>> BuildOptionsAsync(Guid customerId, CancellationToken ct)
    {
        var customer = await _centralDb.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct)
            .ConfigureAwait(false);

        if (customer is null)
        {
            throw new InvalidOperationException($"Customer '{customerId}' not found in CentralDb.");
        }

        var connectionString = await _secretManager
            .DecryptAsync(customer.EncryptedConnectionString, customer.EncryptionKeyVersion, ct)
            .ConfigureAwait(false);

        var builder = new DbContextOptionsBuilder<CustomerDbContext>();
        builder.UseSqlServer(connectionString);

        return builder.Options;
    }
}

