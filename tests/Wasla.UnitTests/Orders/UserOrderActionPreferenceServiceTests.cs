using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// The per-employee choice to confirm Approve and Mark ready (WAS-119): no record means confirm, and a change affects
/// only that employee in that tenant.
/// </summary>
public sealed class UserOrderActionPreferenceServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private readonly TenantSqlite _tenants = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserOrderActionPreferenceService Service() => new(_tenants, TimeProvider.System);

    [Fact]
    public async Task NoRecord_MeansConfirm_AndReadingCreatesNothing()
    {
        await SeedAsync(TenantA, UserA);

        Assert.True(await Service().GetConfirmRoutineActionsAsync(TenantA, UserA, Ct));
        Assert.Equal(0, await CountAsync(TenantA));
    }

    [Fact]
    public async Task Set_StoresOneRow_ReturnsTheStoredValue_AndCanBeChangedBack()
    {
        await SeedAsync(TenantA, UserA);
        var service = Service();

        Assert.False(await service.SetConfirmRoutineActionsAsync(TenantA, UserA, false, Ct));
        Assert.False(await service.GetConfirmRoutineActionsAsync(TenantA, UserA, Ct));
        Assert.Equal(1, await CountAsync(TenantA));

        Assert.True(await service.SetConfirmRoutineActionsAsync(TenantA, UserA, true, Ct));
        Assert.True(await service.GetConfirmRoutineActionsAsync(TenantA, UserA, Ct));
        Assert.Equal(1, await CountAsync(TenantA));
    }

    [Fact]
    public async Task AChange_AffectsOnlyThatEmployee_InThatTenant()
    {
        await SeedAsync(TenantA, UserA, UserB);
        await SeedAsync(TenantB, UserA);
        var service = Service();

        await service.SetConfirmRoutineActionsAsync(TenantA, UserA, false, Ct);

        Assert.False(await service.GetConfirmRoutineActionsAsync(TenantA, UserA, Ct));
        Assert.True(await service.GetConfirmRoutineActionsAsync(TenantA, UserB, Ct));
        Assert.True(await service.GetConfirmRoutineActionsAsync(TenantB, UserA, Ct));
    }

    public void Dispose() => _tenants.Dispose();

    private async Task SeedAsync(Guid tenantId, params Guid[] userIds)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        foreach (var userId in userIds)
        {
            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Email = userId.ToString("N") + "@example.test",
                PasswordHash = "hash",
                FullName = "Preference User",
                Role = UserRole.Kitchen,
                IsActive = true
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<int> CountAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        return await db.UserOrderActionPreferences.CountAsync(Ct);
    }

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(tenantId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(tenantId, connection);
                using var setup = new PreferenceTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new PreferenceTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class PreferenceTenantDbContext : TenantDbContext
    {
        public PreferenceTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppUser>(builder =>
            {
                builder.ToTable("AppUsers");
                builder.HasKey(user => user.Id);
                builder.Property(user => user.Email).IsRequired();
                builder.Property(user => user.PasswordHash).IsRequired();
            });

            modelBuilder.ApplyConfiguration(new Wasla.Infrastructure.Persistence.Tenant.Configurations.UserOrderActionPreferenceConfiguration());
        }
    }
}
