using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-18: the shared ingestion path behind polling (<see cref="OrderIngestion"/>). Full observations are refused when
/// the provider's own supplier or store contradicts the connection; repeated observations change nothing; the status
/// merge keeps stale and terminal rules; and a status-only observation can never create an order or touch its contents.
/// SQLite; the concurrent cases run on SQL Server in <see cref="OrderIngestionInsertRaceSqlServerTests"/>.
/// </summary>
public sealed class OrderIngestionTests : IDisposable
{
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTenants _tenants = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly FakeClient _client = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingReceipts _receipts = new();
    private readonly RecordingAutoApprove _autoApprove = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    // ---- Ownership of full observations ----

    [Fact]
    public async Task PackageReportingAnotherStore_IsNotStored_AndTheWindowStillCompletes()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        _client.Set(Package("pkg-other-store", "Created") with { ProviderSupplierId = "900001", ProviderStoreId = "900099" });

        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(1, result.SkippedCount);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
        var connection = await db.PlatformConnections.SingleAsync(Ct);
        Assert.Equal(0, connection.ConsecutiveFailures);
        Assert.NotNull(connection.LastSuccessfulSync);
        Assert.Empty(_autoApprove.Processed);
        Assert.Empty(_notifier.All);
    }

    [Fact]
    public async Task PackageReportingAnotherSupplier_IsNotStored()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        _client.Set(Package("pkg-other-supplier", "Created") with { ProviderSupplierId = "900077", ProviderStoreId = "900002" });

        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(1, result.SkippedCount);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
    }

    [Fact]
    public async Task MatchingOrMissingProviderIdentifiers_AreStored()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        _client.Set(
            Package("pkg-both-match", "Created") with { ProviderSupplierId = "900001", ProviderStoreId = " 900002 " },
            Package("pkg-store-only", "Created") with { ProviderStoreId = "900002" },
            Package("pkg-no-identifiers", "Created"));

        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(3, result.InsertedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public async Task ConnectionWithoutSupplierId_ComparesOnlyTheStore()
    {
        // Such a connection polls with its StoreId as the supplier id; the reported supplier is not compared.
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: null);
        _client.Set(
            Package("pkg-any-supplier", "Created") with { ProviderSupplierId = "900001", ProviderStoreId = "900002" },
            Package("pkg-wrong-store", "Created") with { ProviderSupplierId = "900001", ProviderStoreId = "900003" });

        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(1, result.SkippedCount);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Equal("pkg-any-supplier", (await db.Orders.SingleAsync(Ct)).ExternalOrderId);
    }

    [Fact]
    public async Task PackageOfOneTenantsStore_ReachesOnlyThatTenant()
    {
        await SeedTenantAsync(_tenantId, storeId: "store-a", supplierId: "supplier-1");
        await SeedTenantAsync(_otherTenantId, storeId: "store-b", supplierId: "supplier-1");
        // A provider response that wrongly lists tenant A's package for tenant B's store as well.
        _client.Set(Package("pkg-of-store-a", "Created") with { ProviderSupplierId = "supplier-1", ProviderStoreId = "store-a" });

        var forA = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        var forB = await Sync().SyncCustomerWithResultAsync(_otherTenantId, Ct);

        Assert.Equal(1, forA.InsertedCount);
        Assert.Equal(0, forB.InsertedCount);
        Assert.Equal(1, forB.SkippedCount);
        Assert.Equal(0, forB.FailedConnections);
        await using var a = await _tenants.CreateAsync(_tenantId, Ct);
        await using var b = await _tenants.CreateAsync(_otherTenantId, Ct);
        Assert.Single(await a.Orders.ToListAsync(Ct));
        Assert.Empty(await b.Orders.ToListAsync(Ct));
    }

    [Fact]
    public async Task StoredOrderReportedLaterForAnotherStore_IsLeftAsItWas()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        var stored = Package("pkg-moves", "Created") with { ProviderStoreId = "900002" };
        _client.Set(stored);
        await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        var before = await SingleOrderAsync("pkg-moves");

        _client.Set(stored with { ExternalStatus = "Picking", CustomerName = "Someone Else", ProviderStoreId = "900099" });
        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(1, result.SkippedCount);
        var after = await SingleOrderAsync("pkg-moves");
        Assert.Equal(before.ChangeVersion, after.ChangeVersion);
        Assert.Equal(OrderStatus.New, after.InternalStatus);
        Assert.Equal(before.CustomerName, after.CustomerName);
        Assert.Empty(_receipts.Created);
    }

    [Fact]
    public void Contradiction_IsDecidedOnlyByReportedAndConfiguredValues()
    {
        var connection = new PlatformConnection { Platform = FoodPlatform.TrendyolYemek, StoreId = "900002", SupplierId = "900001" };
        var package = Package("pkg-classify", "Created");

        Assert.Null(OrderIngestion.ContradictsConnection(connection, package));
        Assert.Null(OrderIngestion.ContradictsConnection(connection, package with { ProviderStoreId = "  ", ProviderSupplierId = "" }));
        Assert.Equal("store", OrderIngestion.ContradictsConnection(connection, package with { ProviderStoreId = "900003" }));
        Assert.Equal("supplier", OrderIngestion.ContradictsConnection(connection, package with { ProviderSupplierId = "900009" }));
        Assert.Equal("platform", OrderIngestion.ContradictsConnection(connection, package with { Platform = FoodPlatform.Yemeksepeti }));

        // All-digit identifiers compare as numbers: a JSON number 123 is the store typed in as "00123", and back.
        var zeroPadded = new PlatformConnection { Platform = FoodPlatform.TrendyolYemek, StoreId = "00123", SupplierId = "0900001" };
        Assert.Null(OrderIngestion.ContradictsConnection(zeroPadded, package with { ProviderStoreId = "123", ProviderSupplierId = "900001" }));
        Assert.Null(OrderIngestion.ContradictsConnection(connection, package with { ProviderStoreId = "0900002", ProviderSupplierId = "00900001" }));
        Assert.Equal("store", OrderIngestion.ContradictsConnection(zeroPadded, package with { ProviderStoreId = "1230" }));
        Assert.Equal("store", OrderIngestion.ContradictsConnection(
            new PlatformConnection { Platform = FoodPlatform.TrendyolYemek, StoreId = "A-0123" }, package with { ProviderStoreId = "A-123" }));
    }

    // ---- Repeated observations ----

    [Fact]
    public async Task SameAcceptedPackageObservedThreeTimes_IsOneOrder_WithItsSideEffectsOnce()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        _client.Set(Package("pkg-replay", "Picking"));

        var results = new List<OrderSyncCustomerResult>();
        for (var i = 0; i < 3; i++)
            results.Add(await Sync().SyncCustomerWithResultAsync(_tenantId, Ct));

        Assert.Equal([1, 0, 0], results.Select(r => r.InsertedCount));
        Assert.Equal([0, 1, 1], results.Select(r => r.UnchangedCount));
        var order = await SingleOrderAsync("pkg-replay");
        Assert.Equal(OrderChangeVersions.Initial, order.ChangeVersion);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
    }

    // ---- Status merge through the full observation path ----

    [Theory]
    // A provider status that moves the order on is applied.
    [InlineData(OrderStatus.New, "Picking", OrderStatus.Accepted)]
    [InlineData(OrderStatus.ReadyForPickup, "Shipped", OrderStatus.OnTheWay)]
    [InlineData(OrderStatus.OnTheWay, "Delivered", OrderStatus.Delivered)]
    // A stale provider status never moves the order back.
    [InlineData(OrderStatus.Accepted, "Created", OrderStatus.Accepted)]
    [InlineData(OrderStatus.Preparing, "Picking", OrderStatus.Preparing)]
    [InlineData(OrderStatus.OnTheWay, "Invoiced", OrderStatus.OnTheWay)]
    // A provider cancellation overrides progress that is not terminal.
    [InlineData(OrderStatus.Preparing, "UnSupplied", OrderStatus.Cancelled)]
    [InlineData(OrderStatus.OnTheWay, "Cancelled", OrderStatus.Cancelled)]
    // Terminal statuses are kept; delivery and cancellation do not overwrite each other.
    [InlineData(OrderStatus.Delivered, "Cancelled", OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled, "Delivered", OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, "Picking", OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Delivered, "Created", OrderStatus.Delivered)]
    [InlineData(OrderStatus.Failed, "Picking", OrderStatus.Failed)]
    public async Task ProviderStatusOnAStoredOrder_FollowsTheMerge(OrderStatus stored, string providerStatus, OrderStatus expected)
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        var orderId = await StoreOrderWithStatusAsync("pkg-merge", stored);

        _client.Set(Package("pkg-merge", providerStatus));
        var result = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, result.FailedConnections);
        var order = await SingleOrderAsync("pkg-merge");
        Assert.Equal(expected, order.InternalStatus);
        // The provider-accepted receipt belongs only to the move into Accepted.
        Assert.Equal(expected == OrderStatus.Accepted && stored != OrderStatus.Accepted ? [orderId] : [], _receipts.Created);
    }

    // ---- Status-only observations ----

    [Fact]
    public async Task StatusOnlyObservationOfAnUnknownOrder_CreatesNothing()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);

        var result = await Ingestion().ApplyStatusObservationAsync(
            _tenantId, db, connection, new OrderStatusObservation(FoodPlatform.TrendyolYemek, "pkg-unknown", OrderStatus.Delivered, "Delivered"),
            milestonesObservedLive: true, Ct);

        Assert.True(result.Skipped);
        await using var check = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Empty(await check.Orders.ToListAsync(Ct));
        Assert.Empty(_notifier.All);
    }

    [Fact]
    public async Task StatusOnlyObservation_MovesTheStatus_WithoutTouchingItemsOrCustomerFields()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        var orderId = await StoreOrderWithStatusAsync("pkg-thin", OrderStatus.ReadyForPickup);
        var before = await SingleOrderAsync("pkg-thin");
        var itemsBefore = await ItemRowsAsync(orderId);

        var result = await ApplyStatusAsync("pkg-thin", OrderStatus.OnTheWay, "Shipped");

        Assert.True(result.Updated);
        var after = await SingleOrderAsync("pkg-thin");
        Assert.Equal(OrderStatus.OnTheWay, after.InternalStatus);
        Assert.Equal("Shipped", after.PlatformStatus);
        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        Assert.Equal(before.CustomerName, after.CustomerName);
        Assert.Equal(before.CustomerPhone, after.CustomerPhone);
        Assert.Equal(before.CustomerAddress, after.CustomerAddress);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.RawPayloadJson, after.RawPayloadJson);
        Assert.Equal(before.CreatedAtPlatform, after.CreatedAtPlatform);
        Assert.Equal(itemsBefore, await ItemRowsAsync(orderId));
        Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
    }

    [Theory]
    [InlineData(OrderStatus.OnTheWay, OrderStatus.Accepted)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Cancelled)]
    public async Task StaleOrTerminalStatusOnlyObservation_WritesNothing(OrderStatus stored, OrderStatus observed)
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        await StoreOrderWithStatusAsync("pkg-thin-stale", stored);
        var before = await SingleOrderAsync("pkg-thin-stale");

        var result = await ApplyStatusAsync("pkg-thin-stale", observed, "Stale");

        Assert.True(result.Unchanged);
        var after = await SingleOrderAsync("pkg-thin-stale");
        Assert.Equal(stored, after.InternalStatus);
        Assert.Equal(before.PlatformStatus, after.PlatformStatus);
        Assert.Equal(before.ChangeVersion, after.ChangeVersion);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(_notifier.All);
    }

    [Fact]
    public async Task StatusOnlyObservationIntoAccepted_CreatesOneReceipt_ARepeatNone()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        var orderId = await StoreOrderWithStatusAsync("pkg-thin-accepted", OrderStatus.New);

        Assert.True((await ApplyStatusAsync("pkg-thin-accepted", OrderStatus.Accepted, "Picking")).Updated);
        Assert.True((await ApplyStatusAsync("pkg-thin-accepted", OrderStatus.Accepted, "Picking")).Unchanged);

        Assert.Equal([orderId], _receipts.Created);
        Assert.Empty(_autoApprove.Processed);
    }

    [Fact]
    public async Task StatusOnlyObservationForAnotherPlatform_IsRefused()
    {
        await SeedTenantAsync(_tenantId, storeId: "900002", supplierId: "900001");
        await StoreOrderWithStatusAsync("pkg-thin-platform", OrderStatus.New);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);

        var result = await Ingestion().ApplyStatusObservationAsync(
            _tenantId, db, connection, new OrderStatusObservation(FoodPlatform.Yemeksepeti, "pkg-thin-platform", OrderStatus.Cancelled, null),
            milestonesObservedLive: true, Ct);

        Assert.True(result.Skipped);
        Assert.Equal(OrderStatus.New, (await SingleOrderAsync("pkg-thin-platform")).InternalStatus);
    }

    // ---- Helpers ----

    private async Task<OrderUpsertResult> ApplyStatusAsync(string externalOrderId, OrderStatus status, string platformStatus)
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);
        return await Ingestion().ApplyStatusObservationAsync(
            _tenantId,
            db,
            connection,
            new OrderStatusObservation(FoodPlatform.TrendyolYemek, externalOrderId, status, platformStatus),
            milestonesObservedLive: true,
            Ct);
    }

    /// <summary>Stores the order through a sync pass, then sets its status as an operator or earlier sync would have.</summary>
    private async Task<Guid> StoreOrderWithStatusAsync(string externalOrderId, OrderStatus status)
    {
        _client.Set(Package(externalOrderId, "Created"));
        await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var order = await db.Orders.SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
        order.InternalStatus = status;
        await db.SaveChangesAsync(Ct);
        _notifier.Reset();
        _receipts.Reset();
        _autoApprove.Reset();
        return order.Id;
    }

    private async Task<Order> SingleOrderAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private async Task<string[]> ItemRowsAsync(Guid orderId)
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var items = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == orderId)
            .OrderBy(i => i.Sequence).ToListAsync(Ct);
        return items.Select(i => $"{i.Id}|{i.ProductName}|{i.Quantity}|{i.TotalPrice}|{i.Notes}").ToArray();
    }

    private async Task SeedTenantAsync(Guid tenantId, string storeId, string? supplierId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Id = Guid.NewGuid(),
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = storeId,
            SupplierId = supplierId,
            EncryptedApiKey = "fake-key",
            EncryptedApiSecret = "fake-secret",
            IsActive = true,
            SyncIntervalSeconds = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.TenantOperationalSettings.Add(new TenantOperationalSettings
        {
            Id = SettingsId,
            OrderSyncEnabled = true,
            OperationalMode = TenantOperationalMode.Live,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    private OrderSyncService Sync() =>
        new(
            _tenants,
            [_client],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            _autoApprove,
            _receipts,
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private OrderIngestion Ingestion() =>
        new(
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            _autoApprove,
            _receipts,
            _notifier,
            NullLogger<OrderIngestionTests>.Instance);

    private static ExternalOrderDto Package(string externalOrderId, string providerStatus) =>
        new(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId,
            new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc),
            "Ingestion Customer",
            "5000000000",
            "Synthetic Street 1",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.CreditCard,
            PaymentStatus.Paid,
            providerStatus,
            "{}",
            [new ExternalOrderItemDto(externalOrderId + "-line-1", "Lahmacun", 1, 100m, 100m, "No onions", [new ExternalOrderItemOptionDto("Spicy", 0m)])]);

    private sealed class FakeClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : ITenantChangeNotifier
    {
        private readonly ConcurrentQueue<TenantChange> _changes = new();

        public IReadOnlyList<TenantChange> All => _changes.ToList();

        public void Reset() => _changes.Clear();

        public IReadOnlyList<long> VersionsFor(Guid orderId) =>
            _changes.OfType<OrderChanged>().Where(c => c.OrderId == orderId).Select(c => c.Version).ToList();

        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReceipts : IOrderReceiptCreationService
    {
        private readonly ConcurrentQueue<Guid> _created = new();
        public IReadOnlyList<Guid> Created => _created.ToList();

        public void Reset() => _created.Clear();

        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            _created.Enqueue(orderId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAutoApprove : IOrderAutoApproveService
    {
        private readonly ConcurrentQueue<Guid> _processed = new();
        public IReadOnlyList<Guid> Processed => _processed.ToList();

        public void Reset() => _processed.Clear();

        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct)
        {
            _processed.Enqueue(orderId);
            return Task.CompletedTask;
        }
    }

    /// <summary>One in-memory SQLite database per tenant, with the order model the sync path writes.</summary>
    private sealed class SqliteTenants : ITenantDbContextFactory, IDisposable
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

        private sealed class TestTenantDbContext(DbContextOptions<TenantDbContext> options) : TenantDbContext(options)
        {
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

                modelBuilder.Entity<Order>(builder =>
                {
                    builder.ToTable("Orders");
                    builder.HasKey(x => x.Id);
                    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
                    builder.Property(x => x.OrderedAt).HasComputedColumnSql(Wasla.Infrastructure.Persistence.Tenant.Configurations.OrderConfiguration.OrderedAtSql, stored: true);
                    builder.HasMany(x => x.Items).WithOne(x => x.Order).HasForeignKey(x => x.OrderId);
                });

                modelBuilder.Entity<OrderItem>(builder =>
                {
                    builder.ToTable("OrderItems");
                    builder.HasKey(x => x.Id);
                    builder.HasMany(x => x.Options).WithOne(x => x.OrderItem).HasForeignKey(x => x.OrderItemId);
                });

                modelBuilder.Entity<OrderItemOption>(builder =>
                {
                    builder.ToTable("OrderItemOptions");
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
}
