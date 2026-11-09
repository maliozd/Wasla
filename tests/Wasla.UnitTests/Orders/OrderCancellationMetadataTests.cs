using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Orders;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-200: how a cancelled order ended. An operator's rejection and its validated reason are stored with the status and
/// only after the provider accepted it; a provider's report stores only what its documented status proves; and nothing
/// that is already known, or already ended unknown, is changed later. SQLite over the real tenant model, so the check
/// constraint applies too; races and the migration run on SQL Server in <see cref="OrderCancellationMetadataSqlServerTests"/>
/// and <see cref="OrderCancellationMigrationSqlServerTests"/>.
/// </summary>
public sealed class OrderCancellationMetadataTests : IDisposable
{
    private const string InvalidStatus = "Orders.InvalidStatusForAction";

    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTenants _tenants = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly FakeClient _trendyol = new(FoodPlatform.TrendyolYemek);
    private readonly FakeClient _yemeksepeti = new(FoodPlatform.Yemeksepeti);
    private readonly FakeClient _getir = new(FoodPlatform.GetirYemek);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    // ---- Operator rejection ----

    [Theory]
    [InlineData(621)]
    [InlineData(622)]
    [InlineData(623)]
    [InlineData(627)]
    public async Task TrendyolReject_StoresTheChosenReasonWithItsPlatform_InTheStatusWrite(int reasonId)
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-reject");
        var before = await ReadAsync(_tenantId, orderId);

        var result = await Actions().TryRejectAsync(_tenantId, orderId, reasonId, Ct);

        Assert.Equal(new OrderActionResult(true, "Orders.RejectSuccess"), result);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.NotNull(after.CancelledAt);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Equal(FoodPlatform.TrendyolYemek, after.CancellationReasonPlatform);
        Assert.Equal(reasonId, after.CancellationReasonCode);
        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        Assert.Equal([reasonId], _trendyol.RejectReasons);
    }

    [Theory]
    [InlineData(null, OrderRejectReasons.ReasonRequiredKey)]
    [InlineData(0, OrderRejectReasons.ReasonNotSupportedKey)]
    [InlineData(624, OrderRejectReasons.ReasonNotSupportedKey)]
    [InlineData(626, OrderRejectReasons.ReasonNotSupportedKey)]
    [InlineData(601, OrderRejectReasons.ReasonNotSupportedKey)]
    [InlineData(604, OrderRejectReasons.ReasonNotSupportedKey)]
    [InlineData(999, OrderRejectReasons.ReasonNotSupportedKey)]
    public async Task TrendyolReject_WithAMissingOrUnsupportedReason_StoresNothing(int? reasonId, string expectedKey)
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-bad-reason");

        var result = await Actions().TryRejectAsync(_tenantId, orderId, reasonId, Ct);

        Assert.Equal(new OrderActionResult(false, expectedKey), result);
        Assert.Empty(_trendyol.RejectReasons);
        await AssertUntouchedAsync(_tenantId, orderId, OrderStatus.New);
    }

    [Theory]
    [InlineData(FoodPlatform.Yemeksepeti)]
    [InlineData(FoodPlatform.GetirYemek)]
    public async Task PlatformsWithoutReasons_StoreTheRejectionWithoutAReason(FoodPlatform platform)
    {
        var orderId = await SeedOrderAsync(_tenantId, platform, "order-no-reasons");

        var result = await Actions().TryRejectAsync(_tenantId, orderId, null, Ct);

        Assert.True(result.Succeeded);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Null(after.CancellationReasonPlatform);
        Assert.Null(after.CancellationReasonCode);
    }

    [Theory]
    [InlineData(FoodPlatform.Yemeksepeti)]
    [InlineData(FoodPlatform.GetirYemek)]
    public async Task PlatformsWithoutReasons_RefuseAReasonAndStoreNothing(FoodPlatform platform)
    {
        var orderId = await SeedOrderAsync(_tenantId, platform, "order-invented-reason");

        var result = await Actions().TryRejectAsync(_tenantId, orderId, 621, Ct);

        Assert.Equal(new OrderActionResult(false, OrderRejectReasons.ReasonNotSupportedKey), result);
        await AssertUntouchedAsync(_tenantId, orderId, OrderStatus.New);
    }

    [Fact]
    public async Task ProviderRefusal_LeavesStatusAndMetadataUnchanged_AndARetryStoresOnlyItsOwnReason()
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-refused");
        var before = await ReadAsync(_tenantId, orderId);
        _trendyol.FailNextReject = new HttpRequestException("provider refused");

        var refused = await Actions().TryRejectAsync(_tenantId, orderId, 621, Ct);

        Assert.Equal(new OrderActionResult(false, "Orders.RejectFailed"), refused);
        await AssertUntouchedAsync(_tenantId, orderId, OrderStatus.New);
        Assert.Equal(before.ChangeVersion, (await ReadAsync(_tenantId, orderId)).ChangeVersion);

        var retried = await Actions().TryRejectAsync(_tenantId, orderId, 622, Ct);

        Assert.True(retried.Succeeded);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Equal(622, after.CancellationReasonCode);
    }

    [Fact]
    public async Task ARejectForAnOrderNoLongerNew_ChangesNothing_NotEvenTheReason()
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-repeat");
        Assert.True((await Actions().TryRejectAsync(_tenantId, orderId, 621, Ct)).Succeeded);
        var first = await ReadAsync(_tenantId, orderId);

        var repeated = await Actions().TryRejectAsync(_tenantId, orderId, 623, Ct);

        Assert.Equal(new OrderActionResult(false, InvalidStatus), repeated);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(621, after.CancellationReasonCode);
        Assert.Equal(first.ChangeVersion, after.ChangeVersion);
        Assert.Equal([621], _trendyol.RejectReasons);
    }

    [Fact]
    public async Task ApprovingAndTheStatusOnlyLegacyPath_RecordNoOrigin()
    {
        var approvedId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-approved");
        Assert.True((await Actions().TryApproveAsync(_tenantId, approvedId, Ct)).Succeeded);
        await AssertUntouchedAsync(_tenantId, approvedId, OrderStatus.Preparing);

        // The legacy path makes no provider call and carries no operator intent: Wasla cannot say how it ended.
        var legacyId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-legacy");
        Assert.True(await Actions().TryUpdateStatusAsync(_tenantId, legacyId, OrderStatus.Cancelled, Ct));
        await AssertUntouchedAsync(_tenantId, legacyId, OrderStatus.Cancelled);
    }

    // ---- What sync may record ----

    [Theory]
    [InlineData(FoodPlatform.TrendyolYemek, "UnSupplied", OrderCancellationOrigin.RestaurantOnPlatform)]
    [InlineData(FoodPlatform.TrendyolYemek, "Cancelled", OrderCancellationOrigin.ProviderOrCustomer)]
    // Maps to Cancelled, but its meaning is not documented.
    [InlineData(FoodPlatform.TrendyolYemek, "Returned", null)]
    [InlineData(FoodPlatform.TrendyolYemek, "İptal", null)]
    // A cancelled status alone says nothing about who cancelled on these platforms.
    [InlineData(FoodPlatform.Yemeksepeti, "CANCELLED", null)]
    [InlineData(FoodPlatform.Yemeksepeti, "Cancelled", null)]
    [InlineData(FoodPlatform.GetirYemek, "CANCELLED", null)]
    [InlineData(FoodPlatform.GetirYemek, "Cancelled", null)]
    public async Task StoredOrderCancelledByTheProvider_RecordsOnlyWhatTheStatusProves(
        FoodPlatform platform, string providerStatus, OrderCancellationOrigin? expected)
    {
        var orderId = await SeedOrderAsync(_tenantId, platform, "order-observed");
        Client(platform).Set(Package(platform, "order-observed", providerStatus));

        var result = await Sync().SyncTenantWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, result.FailedConnections);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(expected, after.CancellationOrigin);
        Assert.Null(after.CancellationReasonPlatform);
        Assert.Null(after.CancellationReasonCode);
    }

    [Theory]
    [InlineData("UnSupplied", OrderCancellationOrigin.RestaurantOnPlatform)]
    [InlineData("Cancelled", OrderCancellationOrigin.ProviderOrCustomer)]
    public async Task OrderFirstSeenAlreadyCancelled_InRecovery_RecordsTheOriginButNoTime(string providerStatus, OrderCancellationOrigin expected)
    {
        await SeedConnectionAsync(_tenantId, FoodPlatform.TrendyolYemek);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);

        var result = await Ingestion().IngestPackageAsync(
            _tenantId, db, connection, Package(FoodPlatform.TrendyolYemek, "pkg-first-seen", providerStatus), milestonesObservedLive: false, Ct);

        Assert.True(result.Inserted);
        var after = await ReadByExternalIdAsync(_tenantId, "pkg-first-seen");
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(expected, after.CancellationOrigin);
        Assert.Null(after.CancelledAt);
        Assert.Null(after.CancellationReasonCode);
    }

    [Theory]
    [InlineData("UnSupplied", OrderCancellationOrigin.RestaurantOnPlatform)]
    [InlineData("Cancelled", OrderCancellationOrigin.ProviderOrCustomer)]
    [InlineData(null, null)]
    public async Task StatusOnlyObservation_RecordsOnlyWhatItsStatusTextProves(string? providerStatus, OrderCancellationOrigin? expected)
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-thin");

        var result = await ApplyObservationAsync("pkg-thin", providerStatus);

        Assert.True(result.Updated);
        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(expected, after.CancellationOrigin);
        Assert.Null(after.CancellationReasonCode);
    }

    [Fact]
    public async Task AProviderReasonInTheRawPayload_IsNotStored()
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-cancel-info");
        const string payload = """{"status":"Cancelled","cancelInfo":{"reasonCode":604,"reason":"Customer Requested Cancellation"}}""";
        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-cancel-info", "Cancelled") with { RawPayloadJson = payload });

        await Sync().SyncTenantWithResultAsync(_tenantId, Ct);

        var after = await ReadAsync(_tenantId, orderId);
        // Even a code that reads like the customer's stays unknown until the field is verified on real orders.
        Assert.Equal(OrderCancellationOrigin.ProviderOrCustomer, after.CancellationOrigin);
        Assert.Null(after.CancellationReasonPlatform);
        Assert.Null(after.CancellationReasonCode);
    }

    // ---- Echoes, repeats and stale polls ----

    [Theory]
    [InlineData("UnSupplied")]
    [InlineData("Cancelled")]
    [InlineData("Created")]
    [InlineData("Picking")]
    [InlineData("Delivered")]
    public async Task OperatorRejection_SurvivesEveryLaterProviderReport(string providerStatus)
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-echo");
        Assert.True((await Actions().TryRejectAsync(_tenantId, orderId, 627, Ct)).Succeeded);
        var rejected = await ReadAsync(_tenantId, orderId);

        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-echo", providerStatus));
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, (await Sync().SyncTenantWithResultAsync(_tenantId, Ct)).FailedConnections);
        await ApplyObservationAsync("pkg-echo", "UnSupplied");

        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(providerStatus, after.PlatformStatus);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Equal(FoodPlatform.TrendyolYemek, after.CancellationReasonPlatform);
        Assert.Equal(627, after.CancellationReasonCode);
        Assert.Equal(rejected.CancelledAt, after.CancelledAt);
    }

    [Fact]
    public async Task AnObservedOrigin_IsNotReplacedByALaterDifferentReport()
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-observed-twice");
        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-observed-twice", "Cancelled"));
        await Sync().SyncTenantWithResultAsync(_tenantId, Ct);

        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-observed-twice", "UnSupplied"));
        await Sync().SyncTenantWithResultAsync(_tenantId, Ct);

        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal("UnSupplied", after.PlatformStatus);
        Assert.Equal(OrderCancellationOrigin.ProviderOrCustomer, after.CancellationOrigin);
    }

    [Theory]
    [InlineData("UnSupplied")]
    [InlineData("Cancelled")]
    public async Task AnOrderThatEndedUnknown_StaysUnknownWhateverIsObservedLater(string providerStatus)
    {
        // An order cancelled before WAS-200 shipped: Cancelled, with no origin and no reason.
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-history", OrderStatus.Cancelled);

        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-history", providerStatus));
        await Sync().SyncTenantWithResultAsync(_tenantId, Ct);
        await ApplyObservationAsync("pkg-history", providerStatus);

        var after = await ReadAsync(_tenantId, orderId);
        Assert.Equal(providerStatus, after.PlatformStatus);
        await AssertUntouchedAsync(_tenantId, orderId, OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ADeliveredOrder_ReportedCancelledLater_RecordsNoOrigin()
    {
        var orderId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-delivered", OrderStatus.Delivered);

        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-delivered", "Cancelled"));
        await Sync().SyncTenantWithResultAsync(_tenantId, Ct);

        await AssertUntouchedAsync(_tenantId, orderId, OrderStatus.Delivered);
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task ARejection_WritesOnlyItsOwnTenantsOrder()
    {
        // The same package id in two tenants' databases: one rejected, the other only observed by its own sync.
        var rejectedId = await SeedOrderAsync(_tenantId, FoodPlatform.TrendyolYemek, "pkg-both-tenants");
        var otherId = await SeedOrderAsync(_otherTenantId, FoodPlatform.TrendyolYemek, "pkg-both-tenants");

        Assert.True((await Actions().TryRejectAsync(_tenantId, rejectedId, 622, Ct)).Succeeded);

        await AssertUntouchedAsync(_otherTenantId, otherId, OrderStatus.New);

        // Knowing another tenant's order id reaches nothing: that order is not in this tenant's database.
        var crossTenant = await Actions().TryRejectAsync(_tenantId, otherId, 621, Ct);
        Assert.Equal(new OrderActionResult(false, "Orders.ActionFailed"), crossTenant);
        await AssertUntouchedAsync(_otherTenantId, otherId, OrderStatus.New);

        _trendyol.Set(Package(FoodPlatform.TrendyolYemek, "pkg-both-tenants", "Cancelled"));
        await Sync().SyncTenantWithResultAsync(_otherTenantId, Ct);

        Assert.Equal(OrderCancellationOrigin.ProviderOrCustomer, (await ReadAsync(_otherTenantId, otherId)).CancellationOrigin);
        var rejected = await ReadAsync(_tenantId, rejectedId);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, rejected.CancellationOrigin);
        Assert.Equal(622, rejected.CancellationReasonCode);
    }

    // ---- Helpers ----

    private FakeClient Client(FoodPlatform platform) => platform switch
    {
        FoodPlatform.TrendyolYemek => _trendyol,
        FoodPlatform.Yemeksepeti => _yemeksepeti,
        _ => _getir
    };

    private OrderActionService Actions() =>
        new(_tenants, [_trendyol, _yemeksepeti, _getir], new NoNotifier(), NullLogger<OrderActionService>.Instance);

    private OrderSyncService Sync() =>
        new(
            _tenants,
            [_trendyol, _yemeksepeti, _getir],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            new NoNotifier(),
            NullLogger<OrderSyncService>.Instance);

    private OrderIngestion Ingestion() =>
        new(
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            new NoNotifier(),
            NullLogger<OrderCancellationMetadataTests>.Instance);

    private async Task<OrderUpsertResult> ApplyObservationAsync(string externalOrderId, string? providerStatus)
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var connection = await db.PlatformConnections.SingleAsync(c => c.Platform == FoodPlatform.TrendyolYemek, Ct);
        return await Ingestion().ApplyStatusObservationAsync(
            _tenantId,
            db,
            connection,
            new OrderStatusObservation(FoodPlatform.TrendyolYemek, externalOrderId, OrderStatus.Cancelled, providerStatus),
            milestonesObservedLive: true,
            Ct);
    }

    private async Task<Guid> SeedOrderAsync(Guid tenantId, FoodPlatform platform, string externalOrderId, OrderStatus status = OrderStatus.New)
    {
        await SeedConnectionAsync(tenantId, platform);
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        var order = new Order
        {
            Id = Guid.NewGuid(),
            Platform = platform,
            ExternalOrderId = externalOrderId,
            ExternalOrderCode = "CODE-" + externalOrderId,
            IdempotencyKey = OrderIngestion.IdempotencyKey(platform, externalOrderId),
            InternalStatus = status,
            PlatformStatus = "Created",
            CustomerName = "Metadata Customer",
            CustomerPhone = "5000000000",
            CustomerAddress = "Synthetic Street 1",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            ReceivedAt = DateTime.UtcNow.AddMinutes(-5),
            RawPayloadJson = "{}",
            ChangeVersion = 1
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.Id;
    }

    private async Task SeedConnectionAsync(Guid tenantId, FoodPlatform platform)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        if (!await db.TenantOperationalSettings.AnyAsync(s => s.Id == SettingsId, Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = true,
                OperationalMode = TenantOperationalMode.Live,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        if (!await db.PlatformConnections.AnyAsync(c => c.Platform == platform, Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(),
                Platform = platform,
                StoreId = "store-metadata",
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                SyncIntervalSeconds = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Order> ReadAsync(Guid tenantId, Guid orderId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task<Order> ReadByExternalIdAsync(Guid tenantId, string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private async Task AssertUntouchedAsync(Guid tenantId, Guid orderId, OrderStatus status)
    {
        var order = await ReadAsync(tenantId, orderId);
        Assert.Equal(status, order.InternalStatus);
        Assert.Null(order.CancellationOrigin);
        Assert.Null(order.CancellationReasonPlatform);
        Assert.Null(order.CancellationReasonCode);
    }

    private static ExternalOrderDto Package(FoodPlatform platform, string externalOrderId, string providerStatus) =>
        new(
            platform,
            externalOrderId,
            "CODE-" + externalOrderId,
            new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc),
            "Metadata Customer",
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
            [new ExternalOrderItemDto(externalOrderId + "-line", "Lahmacun", 1, 100m, 100m, null, [])]);

    private sealed class FakeClient(FoodPlatform platform) : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];

        public FoodPlatform Platform => platform;
        public TimeSpan? MaxFetchWindow => null;
        public Exception? FailNextReject { get; set; }
        public List<int?> RejectReasons { get; } = [];

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public Task RejectOrderAsync(PlatformConnection connection, Order order, int? reasonId, CancellationToken ct)
        {
            if (FailNextReject is { } failure)
            {
                FailNextReject = null;
                return Task.FromException(failure);
            }

            RejectReasons.Add(reasonId);
            return Task.CompletedTask;
        }

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoNotifier : ITenantChangeNotifier
    {
        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>One in-memory SQLite database per tenant, created from the real tenant model.</summary>
    private sealed class SqliteTenants : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(tenantId, out var connection))
            {
                connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                _connections[tenantId] = connection;
                using var setup = new SqliteSchemaTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new SqliteSchemaTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }
}
