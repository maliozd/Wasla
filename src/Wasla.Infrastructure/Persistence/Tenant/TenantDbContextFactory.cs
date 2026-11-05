using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wasla.Application.Abstractions.Security;

namespace Wasla.Infrastructure.Persistence.Tenant;

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

    public async Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
    {
        var lazy = _optionsCache.GetOrAdd(
            tenantId,
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
                new KeyValuePair<Guid, Lazy<Task<DbContextOptions<TenantDbContext>>>>(tenantId, lazy));
            throw;
        }
    }

    private async Task<DbContextOptions<TenantDbContext>> BuildOptionsAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var centralDb = scope.ServiceProvider.GetRequiredService<Wasla.Infrastructure.Persistence.Central.CentralDbContext>();

        var tenant = await centralDb.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == tenantId, ct)
            .ConfigureAwait(false);

        if (tenant is null)
        {
            throw new InvalidOperationException($"Tenant '{tenantId}' not found in CentralDb.");
        }

        var connectionString = await _secretManager
            .DecryptAsync(tenant.EncryptedConnectionString, tenant.EncryptionKeyVersion, ct)
            .ConfigureAwait(false);

        var builder = new DbContextOptionsBuilder<TenantDbContext>();
        builder.UseSqlServer(connectionString);

        return builder.Options;
    }
}

