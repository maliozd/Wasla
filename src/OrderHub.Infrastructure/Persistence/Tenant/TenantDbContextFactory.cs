using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderHub.Application.Abstractions.Security;

namespace OrderHub.Infrastructure.Persistence.Tenant;

public sealed class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretManager _secretManager;

    private readonly ConcurrentDictionary<Guid, Lazy<Task<DbContextOptions<TenantDbContext>>>> _optionsCache = new();

    public TenantDbContextFactory(IServiceScopeFactory scopeFactory, ISecretManager secretManager)
    {
        _scopeFactory = scopeFactory;
        _secretManager = secretManager;
    }

    public async Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
    {
        var lazy = _optionsCache.GetOrAdd(
            customerId,
            id => new Lazy<Task<DbContextOptions<TenantDbContext>>>(
                () => BuildOptionsAsync(id, CancellationToken.None)));

        try
        {
            var options = await lazy.Value.ConfigureAwait(false);
            return new TenantDbContext(options);
        }
        catch
        {
            _optionsCache.TryRemove(
                new KeyValuePair<Guid, Lazy<Task<DbContextOptions<TenantDbContext>>>>(customerId, lazy));
            throw;
        }
    }

    private async Task<DbContextOptions<TenantDbContext>> BuildOptionsAsync(Guid customerId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var centralDb = scope.ServiceProvider.GetRequiredService<OrderHub.Infrastructure.Persistence.Central.CentralDbContext>();

        var customer = await centralDb.Customers
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

        var builder = new DbContextOptionsBuilder<TenantDbContext>();
        builder.UseSqlServer(connectionString);

        return builder.Options;
    }
}

