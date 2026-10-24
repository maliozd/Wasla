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

    [Fact]
    public async Task Notifications_WithoutSavedSettings_AreTheDefaultSoundOn_LikeTheLiveScreen()
    {
        // The 18 Oct 2026 review: no settings row was saved, the Live Screen said the sound was active and the
        // Dashboard said it was off. Both now describe the same effective state.
        var status = await CreateService().GetAsync(_tenantA, Guid.NewGuid(), CancellationToken.None);

        Assert.True(status.Notifications.IsAvailable);
        Assert.True(status.Notifications.IsComplete);
        Assert.False(status.Notifications.CountsTowardReadiness);
    }

    [Fact]
    public async Task Notifications_SavedSoundOff_IsOffForThatUserOnly()
    {
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None))
        {
            db.UserNotificationSettings.Add(new UserNotificationSettings { Id = Guid.NewGuid(), UserId = user, NewOrderSoundEnabled = false });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        Assert.False((await CreateService().GetAsync(_tenantA, user, CancellationToken.None)).Notifications.IsComplete);
        Assert.True((await CreateService().GetAsync(_tenantA, other, CancellationToken.None)).Notifications.IsComplete);
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
            modelBuilder.Entity<UserNotificationSettings>(builder =>
            {
                builder.ToTable("UserNotificationSettings");
                builder.HasKey(settings => settings.Id);
                builder.Ignore(settings => settings.AppUser);
            });
        }
    }
}
