using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Diagnostics;

public sealed class OrderSyncCancellationTests : IDisposable
{
    private readonly SqliteTenantFactory _db = new();
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public async Task HostCancellation_IsNotStoredAsSyncFailure()
    {
        await SeedAsync();
        using var cts = new CancellationTokenSource();
        var sync = CreateSync(new ThrowingClient(() =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sync.SyncCustomerWithResultAsync(_tenantId, cts.Token));

        var ct = TestContext.Current.CancellationToken;
        await using var db = await _db.CreateAsync(_tenantId, ct);
        Assert.Empty(await db.SyncLogs.ToListAsync(ct));
        Assert.Empty(await db.IntegrationErrors.ToListAsync(ct));
    }

    [Fact]
    public async Task HttpTimeout_IsStillStoredAsSyncFailure()
    {
        await SeedAsync();
        var sync = CreateSync(new ThrowingClient(() =>
            throw new TaskCanceledException("HttpClient.Timeout elapsed.")));

        var result = await sync.SyncCustomerWithResultAsync(_tenantId, CancellationToken.None);

        Assert.Equal(1, result.FailedConnections);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await _db.CreateAsync(_tenantId, ct);
        var log = await db.SyncLogs.SingleAsync(ct);
        var error = await db.IntegrationErrors.SingleAsync(ct);
        Assert.Equal(SyncStatus.Failed, log.Status);
        Assert.Equal(log.ErrorMessage, error.ErrorMessage);
        Assert.Contains("Timeout", error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _db.Dispose();

    private OrderSyncService CreateSync(IFoodPlatformClient client) =>
        new(
            _db,
            [client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            NullLogger<OrderSyncService>.Instance);

    private async Task SeedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await _db.CreateAsync(_tenantId, ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "store-1",
            EncryptedApiKey = "encrypted",
            EncryptedApiSecret = "encrypted",
            IsActive = true,
            SyncIntervalSeconds = 0
        });
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            OrderSyncEnabled = true
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed class ThrowingClient(Action throwNow) : IFoodPlatformClient
    {
        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct)
        {
            throwNow();
            return Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);
        }

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class SqliteTenantFactory : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                _connections[customerId] = connection;
                using var setup = new TestTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new TestTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class TestTenantDbContext : TenantDbContext
    {
        public TestTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenantOperationalSettings>(builder =>
            {
                builder.ToTable("TenantOperationalSettings");
                builder.HasKey(x => x.Id);
            });
            modelBuilder.Entity<PlatformConnection>(builder =>
            {
                builder.ToTable("PlatformConnections");
                builder.HasKey(x => x.Id);
            });
            modelBuilder.Entity<SyncLog>(builder =>
            {
                builder.ToTable("SyncLogs");
                builder.HasKey(x => x.Id);
            });
            modelBuilder.Entity<IntegrationError>(builder =>
            {
                builder.ToTable("IntegrationErrors");
                builder.HasKey(x => x.Id);
            });
        }
    }
}
