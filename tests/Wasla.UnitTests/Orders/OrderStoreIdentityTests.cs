using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Abstractions.Security;
using Wasla.Application.Orders;
using Wasla.Application.PlatformConnections;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;
using Wasla.Integrations.Platform;
using Wasla.Integrations.Platform.Dtos;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-241: an order action reaches the provider only with the store the order was imported under. Import records the
/// fetching connection's store and supplier once; an action whose order names another store, or no store, is refused
/// with <c>Orders.StoreNotConfirmed</c> before the provider is called and before anything is written. Orders of the
/// connection's current store behave as before. SQLite over the production tenant model and a recording provider:
/// nothing here calls a real provider or proves SQL Server locking (see <see cref="OrderStoreIdentitySqlServerTests"/>).
/// </summary>
public sealed class OrderStoreIdentityTests : IDisposable
{
    private const string NotConfirmed = "Orders.StoreNotConfirmed";
    private const int RejectReason = 621;

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly RecordingClient _client = new();
    private readonly RecordingNotifier _notifier = new();

    // One provider creation time for every observation of a package, so a repeated observation changes only its status.
    private static readonly DateTime OrderedAt = new(
        DateTime.UtcNow.AddMinutes(-30).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
        DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    public static TheoryData<string> EveryAction => new() { "approve", "reject", "ready" };

    /// <summary>An action, then the connection's store and supplier at the time of the action.</summary>
    public static TheoryData<string, string, string?> AnotherStore
    {
        get
        {
            var data = new TheoryData<string, string, string?>();
            foreach (var action in new[] { "approve", "reject", "ready" })
            {
                data.Add(action, "store-b", "supplier-a");  // the store changed
                data.Add(action, "store-a", "supplier-b");  // the supplier changed
                data.Add(action, "store-a", null);          // the supplier was removed
            }

            return data;
        }
    }

    [Fact]
    public async Task Import_RecordsTheStoreAndSupplierOfTheConnectionTheOrderWasFetchedFor()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        _client.Returns(Package("pkg-1", "Created", providerStoreId: "store-a"));

        await Sync().SyncTenantWithResultAsync(_tenant, Ct);

        var order = await ReadAsync(await OrderIdAsync("pkg-1"));
        Assert.Equal("store-a", order.StoreId);
        Assert.Equal("supplier-a", order.SupplierId);
    }

    [Fact]
    public async Task Import_UnderAConnectionWithoutSupplier_RecordsTheStoreAndNoSupplier()
    {
        await SetUpAsync(storeId: "store-a", supplierId: null);
        _client.Returns(Package("pkg-1", "Created", providerStoreId: null));

        await Sync().SyncTenantWithResultAsync(_tenant, Ct);

        var order = await ReadAsync(await OrderIdAsync("pkg-1"));
        Assert.Equal("store-a", order.StoreId);
        Assert.Null(order.SupplierId);
    }

    [Fact]
    public async Task LaterSyncOfTheOrder_UpdatesItButNeverItsRecordedStore()
    {
        await SetUpAsync(storeId: "00123", supplierId: "supplier-a");
        _client.Returns(Package("pkg-1", "Created", providerStoreId: "123"));
        await Sync().SyncTenantWithResultAsync(_tenant, Ct);
        var orderId = await OrderIdAsync("pkg-1");

        // The same store, typed differently; the provider then reports the order picked.
        await EditConnectionAsync(storeId: "123", supplierId: "supplier-a");
        _client.Returns(Package("pkg-1", "Picking", providerStoreId: "123"));
        await Sync().SyncTenantWithResultAsync(_tenant, Ct);

        var order = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Preparing, order.InternalStatus);
        Assert.Equal("00123", order.StoreId);
        Assert.Equal("supplier-a", order.SupplierId);
    }

    [Fact]
    public async Task Approve_OfAnOrderImportedUnderThePreviousStore_CallsNoProvider_AndKeepsTheOrderNew()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        _client.Returns(Package("pkg-1", "Created", providerStoreId: "store-a"));
        await Sync().SyncTenantWithResultAsync(_tenant, Ct);
        var orderId = await OrderIdAsync("pkg-1");
        var before = await ReadAsync(orderId);
        _notifier.Clear();

        await EditConnectionAsync(storeId: "store-b", supplierId: "supplier-b");
        var result = await Actions().TryApproveAsync(_tenant, orderId, Ct);

        Assert.Equal(new OrderActionResult(false, NotConfirmed), result);
        Assert.Equal(["fetch"], _client.Operations);
        await AssertUnchangedAsync(before);
    }

    [Theory]
    [MemberData(nameof(AnotherStore))]
    public async Task ActionOnAnOrderOfAnotherStore_CallsNoProvider_ChangesNothing_AndPublishesNothing(
        string action,
        string connectionStoreId,
        string? connectionSupplierId)
    {
        await SetUpAsync(storeId: connectionStoreId, supplierId: connectionSupplierId);
        var orderId = await SeedOrderAsync(StatusFor(action), storeId: "store-a", supplierId: "supplier-a");
        var before = await ReadAsync(orderId);

        var result = await ActAsync(action, orderId);

        Assert.Equal(new OrderActionResult(false, NotConfirmed), result);
        Assert.Empty(_client.Operations);
        await AssertUnchangedAsync(before);
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public async Task ActionOnAnOrderImportedWithoutSupplier_IsRefused_OnceTheConnectionHasOne(string action)
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(StatusFor(action), storeId: "store-a", supplierId: null);
        var before = await ReadAsync(orderId);

        var result = await ActAsync(action, orderId);

        Assert.Equal(new OrderActionResult(false, NotConfirmed), result);
        Assert.Empty(_client.Operations);
        await AssertUnchangedAsync(before);
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public async Task ActionOnAnOrderOfTheCurrentStore_IsSentWithThatStore_AsBefore(string action)
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(StatusFor(action), storeId: "store-a", supplierId: "supplier-a");
        var before = await ReadAsync(orderId);

        var result = await ActAsync(action, orderId);

        Assert.Equal(new OrderActionResult(true, SuccessKeyFor(action)), result);
        Assert.Equal([(ProviderCallFor(action), "store-a", (string?)"supplier-a")], _client.Calls);
        var after = await ReadAsync(orderId);
        Assert.Equal(TargetFor(action), after.InternalStatus);
        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public async Task ActionOnAnOrderOfAStoreWithoutSupplier_IsSent_WhileTheConnectionHasNone(string action)
    {
        await SetUpAsync(storeId: "store-a", supplierId: null);
        var orderId = await SeedOrderAsync(StatusFor(action), storeId: "store-a", supplierId: null);

        var result = await ActAsync(action, orderId);

        Assert.Equal(new OrderActionResult(true, SuccessKeyFor(action)), result);
        Assert.Equal([(ProviderCallFor(action), "store-a", (string?)null)], _client.Calls);
        Assert.Equal(TargetFor(action), (await ReadAsync(orderId)).InternalStatus);
    }

    [Theory]
    [InlineData("00123", "0042", "123", "42")]
    [InlineData("store-a", "supplier-a", " store-a ", " supplier-a ")]
    public async Task TheSameStoreWrittenDifferently_IsTheSameStore(
        string orderStoreId,
        string orderSupplierId,
        string connectionStoreId,
        string connectionSupplierId)
    {
        await SetUpAsync(storeId: connectionStoreId, supplierId: connectionSupplierId);
        var orderId = await SeedOrderAsync(OrderStatus.New, orderStoreId, orderSupplierId);

        var result = await Actions().TryApproveAsync(_tenant, orderId, Ct);

        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), result);
        Assert.Equal(["accept"], _client.Operations);
    }

    [Fact]
    public async Task ReplacedCredentialsAndExecutor_KeepTheStoresOrdersActionable()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(OrderStatus.New, "store-a", "supplier-a");

        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            var connection = await db.PlatformConnections.SingleAsync(Ct);
            connection.EncryptedApiKey = "cipher-key-rotated";
            connection.EncryptedApiSecret = "cipher-secret-rotated";
            connection.ExecutorEmail = "another-executor@example.invalid";
            await db.SaveChangesAsync(Ct);
        }

        var result = await Actions().TryApproveAsync(_tenant, orderId, Ct);

        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), result);
        Assert.Equal(["accept"], _client.Operations);
        Assert.Equal(OrderStatus.Preparing, (await ReadAsync(orderId)).InternalStatus);
    }

    [Fact]
    public async Task StoreRemovedAndConnectedAgain_IsStillTheOrdersStore()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(OrderStatus.New, "store-a", "supplier-a");

        await ReconnectAsync(storeId: "store-a", supplierId: "supplier-a");
        var result = await Actions().TryApproveAsync(_tenant, orderId, Ct);

        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), result);
        Assert.Equal(["accept"], _client.Operations);
    }

    [Fact]
    public async Task AnotherStoreConnectedAfterARemoval_IsRefusedForTheEarlierStoresOrders()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(OrderStatus.New, "store-a", "supplier-a");
        var before = await ReadAsync(orderId);

        await ReconnectAsync(storeId: "store-b", supplierId: "supplier-a");
        var result = await Actions().TryApproveAsync(_tenant, orderId, Ct);

        Assert.Equal(new OrderActionResult(false, NotConfirmed), result);
        Assert.Empty(_client.Operations);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task ARefusedStore_ComesAfterTheExistingRefusals()
    {
        // The status, the reject reason and a missing connection are refused as before, with their own messages.
        await SetUpAsync(storeId: "store-b", supplierId: "supplier-a");
        var preparing = await SeedOrderAsync(OrderStatus.Preparing, "store-a", "supplier-a");
        var fresh = await SeedOrderAsync(OrderStatus.New, "store-a", "supplier-a");

        Assert.Equal(new OrderActionResult(false, "Orders.InvalidStatusForAction"), await Actions().TryApproveAsync(_tenant, preparing, Ct));
        Assert.Equal(new OrderActionResult(false, OrderRejectReasons.ReasonRequiredKey), await Actions().TryRejectAsync(_tenant, fresh, null, Ct));

        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
            await db.PlatformConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false), Ct);
        Assert.Equal(new OrderActionResult(false, "Orders.ActionFailed"), await Actions().TryApproveAsync(_tenant, fresh, Ct));
        Assert.Equal(new OrderActionResult(false, "Orders.OrderActionFailed"), await Actions().MarkReadyForPickupAsync(_tenant, preparing, Ct));
        Assert.Empty(_client.Operations);
    }

    // ── Orders stored before the store was recorded ────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryAction))]
    public async Task ActionOnAnOrderStoredBeforeItsStoreWasRecorded_IsRefusedWithoutAProviderCall(string action)
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(StatusFor(action), storeId: null, supplierId: null);
        var before = await ReadAsync(orderId);

        var result = await ActAsync(action, orderId);

        Assert.Equal(new OrderActionResult(false, NotConfirmed), result);
        Assert.Empty(_client.Operations);
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task AnOrderStoredBeforeItsStoreWasRecorded_StillFollowsTheProvider_AndIsNeverGivenAStore()
    {
        await SetUpAsync(storeId: "store-a", supplierId: "supplier-a");
        var orderId = await SeedOrderAsync(OrderStatus.New, storeId: null, supplierId: null, externalOrderId: "pkg-legacy");

        // The operator handles it in the provider's panel; polling brings the outcome in, as for any order.
        _client.Returns(Package("pkg-legacy", "Picking", providerStoreId: "store-a"));
        await Sync().SyncTenantWithResultAsync(_tenant, Ct);

        var order = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Preparing, order.InternalStatus);
        Assert.Null(order.StoreId);
        Assert.Null(order.SupplierId);
        Assert.Equal(new OrderActionResult(false, NotConfirmed), await Actions().MarkReadyForPickupAsync(_tenant, orderId, Ct));
        Assert.Equal(["fetch"], _client.Operations);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static OrderStatus StatusFor(string action) => action == "ready" ? OrderStatus.Preparing : OrderStatus.New;

    private static OrderStatus TargetFor(string action) => action switch
    {
        "approve" => OrderStatus.Preparing,
        "reject" => OrderStatus.Cancelled,
        _ => OrderStatus.ReadyForPickup
    };

    private static string SuccessKeyFor(string action) => action switch
    {
        "approve" => "Orders.ApproveSuccess",
        "reject" => "Orders.RejectSuccess",
        _ => "Orders.MarkReadySuccess"
    };

    private static string ProviderCallFor(string action) => action switch
    {
        "approve" => "accept",
        "reject" => "reject",
        _ => "invoiced"
    };

    private Task<OrderActionResult> ActAsync(string action, Guid orderId) => action switch
    {
        "approve" => Actions().TryApproveAsync(_tenant, orderId, Ct),
        "reject" => Actions().TryRejectAsync(_tenant, orderId, RejectReason, Ct),
        _ => Actions().MarkReadyForPickupAsync(_tenant, orderId, Ct)
    };

    /// <summary>The order as it was: status, times, version and cancellation details, with no notification.</summary>
    private async Task AssertUnchangedAsync(Order before)
    {
        var after = await ReadAsync(before.Id);
        Assert.Equal(before.InternalStatus, after.InternalStatus);
        Assert.Equal(before.ChangeVersion, after.ChangeVersion);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.AcceptedAt, after.AcceptedAt);
        Assert.Equal(before.CancelledAt, after.CancelledAt);
        Assert.Equal(before.CancellationOrigin, after.CancellationOrigin);
        Assert.Equal(before.StoreId, after.StoreId);
        Assert.Equal(before.SupplierId, after.SupplierId);
        Assert.Empty(_notifier.VersionsFor(before.Id));
    }

    private static ExternalOrderDto Package(string id, string status, string? providerStoreId) =>
        new(
            ProviderPlatform.TrendyolYemek,
            id,
            "C-" + id,
            OrderedAt,
            "Test Customer",
            "+900000000000",
            "Test Address",
            Subtotal: 10m,
            DeliveryFee: 0m,
            ServiceFee: 0m,
            Total: 10m,
            ProviderPaymentMethod.CreditCard,
            ProviderPaymentStatus.Paid,
            status,
            StoredPayload(id),
            [])
        {
            ProviderStoreId = providerStoreId
        };

    private static string StoredPayload(string id) =>
        $$"""{"id":"{{id}}","lines":[{"name":"Lahmacun","items":[{"isCancelled":false,"packageItemId":"{{id}}-1"}]}]}""";

    private async Task SetUpAsync(string storeId, string? supplierId)
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await AddConnectionAsync(storeId, supplierId);
    }

    private async Task AddConnectionAsync(string storeId, string? supplierId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Id = Guid.NewGuid(),
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = storeId,
            SupplierId = supplierId,
            ExecutorEmail = "executor@example.invalid",
            EncryptedApiKey = "cipher-key",
            EncryptedApiSecret = "cipher-secret",
            IsActive = true,
            SyncIntervalSeconds = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task EditConnectionAsync(string storeId, string? supplierId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var connection = await db.PlatformConnections.SingleAsync(Ct);
        connection.StoreId = storeId;
        connection.SupplierId = supplierId;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Removes the platform's connection and connects the platform again, as a new row with a new id.</summary>
    private async Task ReconnectAsync(string storeId, string? supplierId)
    {
        var connections = new PlatformConnectionService(_tenants, FakeSecrets.Instance, new CreatePlatformConnectionCommandValidator());
        var existing = Assert.Single(await connections.GetListAsync(_tenant, Ct));
        Assert.True(await connections.RemoveAsync(_tenant, existing.Id, Ct));
        await AddConnectionAsync(storeId, supplierId);
    }

    private async Task<Guid> SeedOrderAsync(OrderStatus status, string? storeId, string? supplierId, string? externalOrderId = null)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var id = Guid.NewGuid();
        externalOrderId ??= "pkg-" + id.ToString("N")[..12];
        var at = OrderedAt;
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalOrderId,
            ExternalOrderCode = "C-" + externalOrderId,
            IdempotencyKey = OrderIngestion.IdempotencyKey(FoodPlatform.TrendyolYemek, externalOrderId),
            StoreId = storeId,
            SupplierId = supplierId,
            InternalStatus = status,
            PlatformStatus = status == OrderStatus.New ? "Created" : "Picking",
            CustomerName = "Test Customer",
            CustomerPhone = "+900000000000",
            CustomerAddress = "Test Address",
            TotalAmount = 10m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = at,
            ReceivedAt = at,
            AcceptedAt = status == OrderStatus.New ? null : at,
            RawPayloadJson = StoredPayload(externalOrderId),
            ChangeVersion = 1,
            CreatedAt = at,
            UpdatedAt = at
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<Guid> OrderIdAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.Where(o => o.ExternalOrderId == externalOrderId).Select(o => o.Id).SingleAsync(Ct);
    }

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private OrderSyncService Sync() =>
        new(
            _tenants,
            [_client],
            FakeSecrets.Instance,
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NoSideEffects.Instance,
            NoSideEffects.Instance,
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private OrderActionService Actions() =>
        new(_tenants, [_client], FakeSecrets.Instance, _notifier, NullLogger<OrderActionService>.Instance);

    /// <summary>Records every call with the store identity it was handed, and returns the packages it was given.</summary>
    private sealed class RecordingClient : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<(string Operation, string StoreId, string? SupplierId)> _calls = new();
        private IReadOnlyCollection<ExternalOrderDto> _fetched = [];

        public ProviderPlatform Platform => ProviderPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;
        public IReadOnlyList<string> Operations => _calls.Select(c => c.Operation).ToList();
        public IReadOnlyList<(string Operation, string StoreId, string? SupplierId)> Calls =>
            _calls.Where(c => c.Operation != "fetch").ToList();

        public void Returns(params ExternalOrderDto[] orders) => _fetched = orders;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(ProviderConnectionContext connection, OrderFetchWindow window, CancellationToken ct)
        {
            Record("fetch", connection);
            return Task.FromResult(_fetched);
        }

        public Task AcceptOrderAsync(ProviderConnectionContext connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Record("accept", connection);
        public Task MarkInvoicedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record("invoiced", connection);
        public Task MarkShippedAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record("shipped", connection);
        public Task MarkDeliveredAsync(ProviderConnectionContext connection, string externalOrderId, CancellationToken ct) => Record("delivered", connection);
        public Task RejectOrderAsync(ProviderConnectionContext connection, ProviderOrderReference order, int? reasonId, CancellationToken ct) => Record("reject", connection);

        private Task Record(string operation, ProviderConnectionContext connection)
        {
            _calls.Enqueue((operation, connection.StoreId, connection.SupplierId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifier : ITenantChangeNotifier
    {
        private readonly ConcurrentQueue<TenantChange> _changes = new();

        public void Clear() => _changes.Clear();

        public IReadOnlyList<long> VersionsFor(Guid orderId) =>
            _changes.OfType<OrderChanged>().Where(c => c.OrderId == orderId).Select(c => c.Version).ToList();

        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecrets : ISecretManager
    {
        public static readonly FakeSecrets Instance = new();

        public Task<(string EncryptedBase64, int KeyVersion)> EncryptAsync(string plaintext, CancellationToken ct) =>
            Task.FromResult(("cipher:" + plaintext, 1));

        public Task<string> DecryptAsync(string encryptedBase64, int keyVersion, CancellationToken ct) =>
            Task.FromResult("plain:" + encryptedBase64);
    }

    private sealed class NoSideEffects : IOrderAutoApproveService, IOrderReceiptCreationService
    {
        public static readonly NoSideEffects Instance = new();

        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;

        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
