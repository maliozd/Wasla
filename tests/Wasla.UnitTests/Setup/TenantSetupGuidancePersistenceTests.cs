using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Domain.Entities.Customer;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Setup;

public sealed class TenantSetupGuidancePersistenceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly TenantSqlite _tenants = new();
    private readonly CentralDbContext _central = new(
        new DbContextOptionsBuilder<CentralDbContext>().UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public async Task CompleteGuidance_PersistsForThatTenantOnly()
    {
        var service = CreateService();

        var completed = await service.CompleteGuidanceAsync(_tenantA, CancellationToken.None);

        Assert.True(completed);
        Assert.NotNull(await ReadCompletedAtAsync(_tenantA));
        Assert.Null(await ReadCompletedAtAsync(_tenantB));
    }

    [Fact]
    public async Task CompleteGuidance_KeepsTheOriginalTimestamp()
    {
        var service = CreateService();

        Assert.True(await service.CompleteGuidanceAsync(_tenantA, CancellationToken.None));
        var first = await ReadCompletedAtAsync(_tenantA);

        Assert.True(await service.CompleteGuidanceAsync(_tenantA, CancellationToken.None));
        var second = await ReadCompletedAtAsync(_tenantA);

        Assert.Equal(first, second);
    }

    private TenantSetupStatusService CreateService() =>
        new(_central, _tenants, new FixedTime(), NullLogger<TenantSetupStatusService>.Instance);

    private async Task<DateTime?> ReadCompletedAtAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        return await db.TenantOperationalSettings
            .AsNoTracking()
            .Select(row => row.SetupGuidanceCompletedAtUtc)
            .FirstOrDefaultAsync();
    }

    public void Dispose()
    {
        _central.Dispose();
        _tenants.Dispose();
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new GuidanceTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new GuidanceTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class GuidanceTenantDbContext : TenantDbContext
    {
        public GuidanceTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenantOperationalSettings>(builder =>
            {
                builder.ToTable("TenantOperationalSettings");
                builder.HasKey(settings => settings.Id);
            });
        }
    }
}
