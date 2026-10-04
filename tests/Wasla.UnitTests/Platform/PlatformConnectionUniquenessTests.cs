using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.PlatformConnections;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Platform;

public sealed class PlatformConnectionUniquenessTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private SqliteConnection _connection = null!;
    private PlatformConnectionService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await using (var db = CreateContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        _service = new PlatformConnectionService(
            new SingleConnectionFactory(_connection),
            new PassthroughSecrets(),
            new CreatePlatformConnectionCommandValidator());
    }

    public ValueTask DisposeAsync()
    {
        PlatformConnectionTenantDbContext.SaveChangesException = null;
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FirstConnection_Succeeds_AndPersistsStoreId()
    {
        var created = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "store-a"), TestContext.Current.CancellationToken);

        Assert.True(created.Succeeded);
        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FoodPlatform.TrendyolYemek, row.Platform);
        Assert.Equal("store-a", row.StoreId);
    }

    [Fact]
    public async Task SamePlatformAndSameStoreId_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await _service.CreateAsync(_tenantId, Command(FoodPlatform.Yemeksepeti, "vendor-1"), ct)).Succeeded);

        var duplicate = await _service.CreateAsync(_tenantId, Command(FoodPlatform.Yemeksepeti, "vendor-1"), ct);

        Assert.False(duplicate.Succeeded);
        Assert.Equal("Duplicate", duplicate.ErrorCode);
        await using var db = CreateContext();
        Assert.Equal(1, await db.PlatformConnections.CountAsync(ct));
    }

    [Fact]
    public async Task SamePlatformAndDifferentStoreId_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await _service.CreateAsync(_tenantId, Command(FoodPlatform.GetirYemek, "store-a"), ct)).Succeeded);

        var duplicate = await _service.CreateAsync(_tenantId, Command(FoodPlatform.GetirYemek, "store-b"), ct);

        Assert.False(duplicate.Succeeded);
        Assert.Equal("Duplicate", duplicate.ErrorCode);
        await using var db = CreateContext();
        Assert.Equal("store-a", await db.PlatformConnections.Select(x => x.StoreId).SingleAsync(ct));
    }

    [Fact]
    public async Task DifferentPlatform_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "ty"), ct)).Succeeded);
        var second = await _service.CreateAsync(_tenantId, Command(FoodPlatform.Yemeksepeti, "ys"), ct);

        Assert.True(second.Succeeded);
        await using var db = CreateContext();
        Assert.Equal(2, await db.PlatformConnections.CountAsync(ct));
    }

    [Fact]
    public async Task EditingTheSameConnection_SucceedsWithoutChangingPlatform()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "old-store"), ct);
        Assert.True(created.Succeeded);

        var updated = await _service.UpdateAsync(
            _tenantId,
            created.Id!.Value,
            Update(FoodPlatform.TrendyolYemek, "new-store"),
            ct);

        Assert.True(updated.Succeeded);
        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(ct);
        Assert.Equal(FoodPlatform.TrendyolYemek, row.Platform);
        Assert.Equal("new-store", row.StoreId);
    }

    [Fact]
    public async Task ChangingPlatformToOneAlreadyUsed_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var trendyol = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "ty"), ct);
        Assert.True((await _service.CreateAsync(_tenantId, Command(FoodPlatform.Yemeksepeti, "ys"), ct)).Succeeded);

        var updated = await _service.UpdateAsync(
            _tenantId,
            trendyol.Id!.Value,
            Update(FoodPlatform.Yemeksepeti, "ty-moved"),
            ct);

        Assert.False(updated.Succeeded);
        Assert.Equal("PlatformImmutable", updated.ErrorCode);
        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(x => x.Id == trendyol.Id, ct);
        Assert.Equal(FoodPlatform.TrendyolYemek, row.Platform);
        Assert.Equal("ty", row.StoreId);
        Assert.Equal("api-key", row.EncryptedApiKey);
    }

    [Fact]
    public async Task ChangingPlatformToAnUnusedPlatform_IsRejectedWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "ty"), ct);

        var updated = await _service.UpdateAsync(
            _tenantId,
            created.Id!.Value,
            new UpdatePlatformConnectionCommand(
                FoodPlatform.GetirYemek,
                "moved",
                false,
                99,
                "supplier",
                "exec@example.com",
                "new-key",
                "new-secret"),
            ct);

        Assert.False(updated.Succeeded);
        Assert.Equal("PlatformImmutable", updated.ErrorCode);
        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(ct);
        Assert.Equal(FoodPlatform.TrendyolYemek, row.Platform);
        Assert.Equal("ty", row.StoreId);
        Assert.True(row.IsActive);
        Assert.Equal(30, row.SyncIntervalSeconds);
        Assert.Equal("api-key", row.EncryptedApiKey);
        Assert.Equal("api-secret", row.EncryptedApiSecret);
        Assert.Null(row.SupplierId);
        Assert.Null(row.ExecutorEmail);
    }

    [Fact]
    public async Task InactiveConnection_StillBlocksAnotherRow_ForSameOrDifferentStoreId()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "store-a"), ct);
        Assert.True(await _service.SetActiveAsync(_tenantId, created.Id!.Value, false, ct));

        var sameStore = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "store-a"), ct);
        var otherStore = await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "store-b"), ct);

        Assert.Equal("Duplicate", sameStore.ErrorCode);
        Assert.Equal("Duplicate", otherStore.ErrorCode);
        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(ct);
        Assert.False(row.IsActive);
        Assert.Equal("store-a", row.StoreId);
    }

    [Fact]
    public async Task UniqueIndexRace_IsReturnedAsDuplicate_AndOtherDatabaseErrorsPropagate()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True((await _service.CreateAsync(_tenantId, Command(FoodPlatform.TrendyolYemek, "store-a"), ct)).Succeeded);

        PlatformConnectionTenantDbContext.SaveChangesException = new DbUpdateException(
            "duplicate",
            new InvalidOperationException(
                "Cannot insert duplicate key row with unique index 'IX_PlatformConnections_Platform'."));
        var raced = await _service.CreateAsync(_tenantId, Command(FoodPlatform.Yemeksepeti, "ys"), ct);
        Assert.Equal("Duplicate", raced.ErrorCode);

        PlatformConnectionTenantDbContext.SaveChangesException = new DbUpdateException(
            "other",
            new InvalidOperationException("UNIQUE constraint failed: AppUsers.Email"));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            _service.CreateAsync(_tenantId, Command(FoodPlatform.GetirYemek, "getir"), ct));

        await using var db = CreateContext();
        var row = await db.PlatformConnections.SingleAsync(ct);
        Assert.Equal(FoodPlatform.TrendyolYemek, row.Platform);
        Assert.Equal("store-a", row.StoreId);
    }

    [Fact]
    public async Task Database_RejectsASecondRowForTheSamePlatform()
    {
        await using var db = CreateContext();
        db.PlatformConnections.Add(Connection(FoodPlatform.TrendyolYemek, "store-a"));
        db.PlatformConnections.Add(Connection(FoodPlatform.TrendyolYemek, "store-b"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private PlatformConnectionTenantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(_connection).Options);

    private static CreatePlatformConnectionCommand Command(FoodPlatform platform, string storeId) =>
        new(platform, storeId, "api-key", "api-secret", true);

    private static UpdatePlatformConnectionCommand Update(FoodPlatform platform, string storeId) =>
        new(platform, storeId, true, null, null, null, null, null);

    private static PlatformConnection Connection(FoodPlatform platform, string storeId) =>
        new()
        {
            Platform = platform,
            StoreId = storeId,
            EncryptedApiKey = "key",
            EncryptedApiSecret = "secret",
            IsActive = true,
            SyncIntervalSeconds = 30
        };

    private sealed class SingleConnectionFactory(SqliteConnection connection) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct) =>
            Task.FromResult<TenantDbContext>(new PlatformConnectionTenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options));
    }

    private sealed class PassthroughSecrets : ISecretManager
    {
        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult((plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult(encryptedBase64);
    }

    /// <summary>
    /// Uses the production <see cref="PlatformConnectionConfiguration"/> without the rest of
    /// TenantDb, whose SQL Server nvarchar(max) columns cannot be created on SQLite.
    /// </summary>
    private sealed class PlatformConnectionTenantDbContext : TenantDbContext
    {
        public static Exception? SaveChangesException { get; set; }

        public PlatformConnectionTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (SaveChangesException is not null)
            {
                var exception = SaveChangesException;
                SaveChangesException = null;
                throw exception;
            }

            return base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new PlatformConnectionConfiguration());
        }
    }
}

public sealed class PlatformConnectionModelTests
{
    [Fact]
    public void Model_UniqueIndexIsPlatformOnly_AndStoreIdRemainsConfigured()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new TenantDbContext(options);
        var entity = db.Model.FindEntityType(typeof(PlatformConnection));
        Assert.NotNull(entity);

        var platformIndex = Assert.Single(entity!.GetIndexes(), index =>
            index.GetDatabaseName() == "IX_PlatformConnections_Platform");
        Assert.True(platformIndex.IsUnique);
        Assert.Null(platformIndex.GetFilter());
        Assert.Equal(nameof(PlatformConnection.Platform), Assert.Single(platformIndex.Properties).Name);
        Assert.DoesNotContain(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "IX_PlatformConnections_Platform_StoreId");

        var storeId = entity.FindProperty(nameof(PlatformConnection.StoreId));
        Assert.NotNull(storeId);
        Assert.Equal(128, storeId!.GetMaxLength());
        Assert.False(storeId.IsNullable);
    }
}
