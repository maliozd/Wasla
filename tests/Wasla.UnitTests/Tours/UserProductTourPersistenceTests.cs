using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Tours;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Tours;

public sealed class UserProductTourPersistenceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly TenantSqlite _tenants = new();

    [Fact]
    public async Task Complete_IsIsolatedByUserAndTenant_AndKeepsTheFirstTimestamp()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        await SeedAsync(_tenantB, _userA, _userB);
        var service = new UserProductTourService(_tenants, new FixedTime(), NullLogger<UserProductTourService>.Instance);

        Assert.True(await service.CompleteAsync(_tenantA, _userA, ProductTourKeys.LiveScreenIntro, CancellationToken.None));
        Assert.True(await service.CompleteAsync(_tenantA, _userA, ProductTourKeys.LiveScreenIntro, CancellationToken.None));

        Assert.True(await service.IsCompletedAsync(_tenantA, _userA, ProductTourKeys.LiveScreenIntro, CancellationToken.None));
        Assert.False(await service.IsCompletedAsync(_tenantA, _userB, ProductTourKeys.LiveScreenIntro, CancellationToken.None));
        Assert.False(await service.IsCompletedAsync(_tenantB, _userA, ProductTourKeys.LiveScreenIntro, CancellationToken.None));
        Assert.False(await service.IsCompletedAsync(_tenantA, _userA, "live-screen-intro:v2", CancellationToken.None));

        var timestamps = await ReadTimestampsAsync(_tenantA, _userA, ProductTourKeys.LiveScreenIntro);
        Assert.Single(timestamps);
        Assert.Equal(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc), timestamps[0]);
    }

    [Fact]
    public async Task Complete_RejectsAKeyThatIsNotVersioned()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        var service = new UserProductTourService(_tenants, new FixedTime(), NullLogger<UserProductTourService>.Instance);

        Assert.False(await service.CompleteAsync(_tenantA, _userA, "live-screen-intro", CancellationToken.None));
        Assert.Empty(await ReadTimestampsAsync(_tenantA, _userA, "live-screen-intro"));
    }

    private async Task SeedAsync(Guid tenantId, params Guid[] userIds)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        foreach (var userId in userIds)
        {
            if (await db.AppUsers.AnyAsync(user => user.Id == userId))
                continue;

            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Email = userId.ToString("N") + "@example.test",
                PasswordHash = "hash",
                FullName = "Tour User",
                Role = UserRole.Owner,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<DateTime>> ReadTimestampsAsync(Guid tenantId, Guid userId, string tourKey)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        return await db.UserProductTourCompletions
            .AsNoTracking()
            .Where(row => row.UserId == userId && row.TourKey == tourKey)
            .Select(row => row.CompletedAtUtc)
            .ToListAsync();
    }

    public void Dispose() => _tenants.Dispose();

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
                using var setup = new TourTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new TourTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class TourTenantDbContext : TenantDbContext
    {
        public TourTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
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

            modelBuilder.Entity<UserProductTourCompletion>(builder =>
            {
                builder.ToTable("UserProductTourCompletions");
                builder.HasKey(row => row.Id);
                builder.Property(row => row.TourKey).HasMaxLength(64).IsRequired();
                builder.HasIndex(row => new { row.UserId, row.TourKey }).IsUnique();
                builder.HasOne(row => row.User)
                    .WithMany()
                    .HasForeignKey(row => row.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
