using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;
using Wasla.Infrastructure.Realtime;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Admin;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The Trendyol GO Meal webhook stage pilot's processor over a SQLite tenant database and a fake windowed Trendyol GO
/// client: stored events become reconciliations or status-only observations through the shared ingestion path, every
/// effect stays single, and the polling state (checkpoint, attempt, failure counters) is never touched.
/// </summary>
public sealed class TrendyolGoMealWebhookProcessorTests : IDisposable
{
    private const string Seller = "107385";
    private const string Store = "1331";
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly RecordingTenantDbFactory _tenants = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeWindowedClient _client = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingReceipts _receipts = new();
    private readonly RecordingAutoApprove _autoApprove = new();
    private readonly TrendyolGoMealWebhookPilotOptions _options;

    public TrendyolGoMealWebhookProcessorTests()
    {
        _tenants.CreateTenantDatabase(_tenantId);
        _options = new TrendyolGoMealWebhookPilotOptions
        {
            Enabled = true,
            TenantId = _tenantId,
            PlatformConnectionId = _connectionId,
            SellerId = Seller,
            StoreId = Store,
            SecretSha256 = new string('0', 64)
        };
        Seed(storeId: Store, supplierId: Seller, orderSyncEnabled: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tenants.Dispose();

    [Fact]
    public async Task CreatedEvent_IsReconciled_StoresTheOrderOnce_AndLeavesPollingStateAlone()
    {
        _client.Set(Package("pkg-1", "Created"));
        await AcceptAsync("created", Created("pkg-1"));

        Assert.Equal(1, await Processor().ProcessDueAsync(10, Ct));

        var stored = Assert.Single(await EventsAsync());
        var order = await SingleOrderAsync("pkg-1");
        Assert.Equal(ProviderWebhookEventStatus.Processed, stored.Status);
        Assert.Equal("Reconciled", stored.Outcome);
        Assert.Equal(order.Id, stored.OrderId);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
        // A short window ending now, from just before the event's own time.
        var window = Assert.Single(_client.Windows);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, window.EndUtc);
        Assert.True(window.EndUtc - window.StartUtc <= TimeSpan.FromHours(1));
        var connection = await ConnectionAsync();
        Assert.Null(connection.LastSuccessfulSync);
        Assert.Null(connection.LastSyncAttempt);
        Assert.Equal(0, connection.ConsecutiveFailures);
    }

    [Fact]
    public async Task DuplicateDeliveriesAndAReplay_StayOneEventAndOneOrder_WithEffectsOnce()
    {
        _client.Set(Package("pkg-dup", "Picking"));
        await AcceptAsync("created", Created("pkg-dup"));
        await AcceptAsync("created", Created("pkg-dup"));
        await Processor().ProcessDueAsync(10, Ct);

        var stored = Assert.Single(await EventsAsync());
        Assert.Equal(2, stored.DeliveryCount);
        Assert.True(await Processor().ReplayAsync(stored.Id, Ct));
        Assert.Equal(1, await Processor().ProcessDueAsync(10, Ct));

        var order = await SingleOrderAsync("pkg-dup");
        Assert.Equal("Reconciled", Assert.Single(await EventsAsync()).Outcome);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
    }

    [Fact]
    public async Task WebhookAfterPollingStoredTheOrder_FindsIt_AndRepeatsNoEffect()
    {
        _client.Set(Package("pkg-polled", "Picking"));
        var polled = await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, polled.InsertedCount);
        var order = await SingleOrderAsync("pkg-polled");

        await AcceptAsync("created", Created("pkg-polled"));
        await Processor().ProcessDueAsync(10, Ct);

        var stored = Assert.Single(await EventsAsync());
        Assert.Equal("Reconciled", stored.Outcome);
        Assert.Equal(order.Id, stored.OrderId);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
    }

    [Fact]
    public async Task CancelledEventForAStoredOrder_AppliesOnlyTheStatus_WithoutAFetch()
    {
        _client.Set(Package("pkg-cancel", "Created"));
        await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        var before = await SingleOrderAsync("pkg-cancel");
        _client.Windows.Clear();
        _client.FailWith(new InvalidOperationException("No fetch is expected."));

        await AcceptAsync("cancelled", Cancellation("pkg-cancel", "cancelled", "Cancelled"));
        await Processor().ProcessDueAsync(10, Ct);

        var stored = Assert.Single(await EventsAsync());
        Assert.Equal("StatusApplied", stored.Outcome);
        var after = await SingleOrderAsync("pkg-cancel");
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(before.CustomerName, after.CustomerName);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Empty(_client.Windows);
        Assert.Equal([before.ChangeVersion, after.ChangeVersion], _notifier.VersionsFor(after.Id));

        // A replay finds the status already applied: nothing changes and nothing is announced again.
        Assert.True(await Processor().ReplayAsync(stored.Id, Ct));
        await Processor().ProcessDueAsync(10, Ct);
        Assert.Equal("StatusUnchanged", Assert.Single(await EventsAsync()).Outcome);
        Assert.Equal(after.ChangeVersion, (await SingleOrderAsync("pkg-cancel")).ChangeVersion);
    }

    [Theory]
    [InlineData("cancelled", "Shipped")]
    [InlineData("cancelled", null)]
    [InlineData("unsupplied", "Cancelled")]
    public async Task CancellationWithoutItsDocumentedStatus_IsReconciled_NotApplied(string eventType, string? status)
    {
        _client.Set(Package("pkg-odd", "Created"));
        await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        _client.Windows.Clear();

        await AcceptAsync(eventType, Cancellation("pkg-odd", eventType, status));
        await Processor().ProcessDueAsync(10, Ct);

        Assert.Equal("Reconciled", Assert.Single(await EventsAsync()).Outcome);
        Assert.Single(_client.Windows);
        Assert.Equal(OrderStatus.New, (await SingleOrderAsync("pkg-odd")).InternalStatus);
    }

    [Theory]
    [InlineData("Shipped", OrderStatus.OnTheWay)]
    [InlineData("Picking", OrderStatus.Accepted)]
    public async Task ShippedEvent_TakesTheStatusFromTheReconciledPackage_NotFromItsName(string providerStatus, OrderStatus expected)
    {
        _client.Set(Package("pkg-ship", "Created"));
        await Sync().SyncCustomerWithResultAsync(_tenantId, Ct);
        _client.Set(Package("pkg-ship", providerStatus));

        await AcceptAsync("shipped", Thin("pkg-ship"));
        await Processor().ProcessDueAsync(10, Ct);

        Assert.Equal("Reconciled", Assert.Single(await EventsAsync()).Outcome);
        Assert.Equal(expected, (await SingleOrderAsync("pkg-ship")).InternalStatus);
    }

    [Fact]
    public async Task EventForAPackageNoReconciliationReturns_IsRetried_ThenFailed_AndCreatesNothing()
    {
        _client.Set();
        await AcceptAsync("delivered", Thin("pkg-unknown"));

        for (var attempt = 1; attempt <= TrendyolGoMealWebhookProcessor.RetryDelays.Length; attempt++)
        {
            Assert.Equal(1, await Processor().ProcessDueAsync(10, Ct));
            var stored = Assert.Single(await EventsAsync());
            Assert.Equal(attempt, stored.Attempts);
            Assert.Equal("PackageNotVisible", stored.Outcome);
            Assert.Equal(attempt < TrendyolGoMealWebhookProcessor.RetryDelays.Length ? ProviderWebhookEventStatus.Pending : ProviderWebhookEventStatus.Failed, stored.Status);
            // Not due again before its backoff ends.
            Assert.Equal(0, await Processor().ProcessDueAsync(10, Ct));
            _clock.Advance(TimeSpan.FromMinutes(10));
        }

        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
        Assert.Empty(_notifier.All);
    }

    [Fact]
    public async Task FailedAttempt_IsRetriedAfterItsBackoff_AndNeverCountsAsAConnectionFailure()
    {
        _client.Set(Package("pkg-retry", "Created"));
        _client.FailWith(new InvalidOperationException("Simulated provider failure"));
        await AcceptAsync("created", Created("pkg-retry"));

        await Processor().ProcessDueAsync(10, Ct);
        var failed = Assert.Single(await EventsAsync());
        Assert.Equal(ProviderWebhookEventStatus.Pending, failed.Status);
        Assert.Equal("Error", failed.Outcome);
        Assert.Equal("Simulated provider failure", failed.LastError);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + TrendyolGoMealWebhookProcessor.RetryDelays[0], failed.NextAttemptAt);

        _client.Succeed();
        Assert.Equal(0, await Processor().ProcessDueAsync(10, Ct));
        _clock.Advance(TrendyolGoMealWebhookProcessor.RetryDelays[0]);
        Assert.Equal(1, await Processor().ProcessDueAsync(10, Ct));

        var processed = Assert.Single(await EventsAsync());
        Assert.Equal("Reconciled", processed.Outcome);
        Assert.Null(processed.LastError);
        var connection = await ConnectionAsync();
        Assert.Equal(0, connection.ConsecutiveFailures);
        Assert.Null(connection.CircuitOpenUntil);
        Assert.Null(connection.LastSuccessfulSync);
    }

    [Theory]
    [InlineData("4242", Seller)]
    [InlineData(Store, "999999")]
    public async Task ConnectionThatIsNotThePilots_IsRefused_WithoutAFetch(string storeId, string supplierId)
    {
        Seed(storeId, supplierId, orderSyncEnabled: true, replace: true);
        _client.Set(Package("pkg-wrong", "Created"));
        await AcceptAsync("created", Thin("pkg-wrong"));

        await Processor().ProcessDueAsync(10, Ct);

        var stored = Assert.Single(await EventsAsync());
        Assert.Equal(ProviderWebhookEventStatus.Rejected, stored.Status);
        Assert.Equal("ConnectionMismatch", stored.Outcome);
        Assert.Empty(_client.Windows);
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        Assert.Empty(await db.Orders.ToListAsync(Ct));
    }

    [Fact]
    public async Task OrderSyncDisabled_ProcessesTheEventWithoutAFetch()
    {
        Seed(Store, Seller, orderSyncEnabled: false, replace: true);
        _client.Set(Package("pkg-off", "Created"));
        await AcceptAsync("created", Created("pkg-off"));

        await Processor().ProcessDueAsync(10, Ct);

        Assert.Equal("OrderSyncDisabled", Assert.Single(await EventsAsync()).Outcome);
        Assert.Empty(_client.Windows);
    }

    [Fact]
    public async Task RejectedAndIgnoredEvents_AreNeverProcessed()
    {
        _client.Set(Package("pkg-x", "Created"));
        await AcceptAsync("created", Created("pkg-x", store: "4242"));
        await AcceptAsync("courierNearby", Thin("pkg-x"));
        await AcceptAsync("storeChanged", Thin("pkg-x"));

        Assert.Equal(0, await Processor().ProcessDueAsync(10, Ct));

        Assert.Empty(_client.Windows);
        Assert.Equal(
            [ProviderWebhookEventStatus.Ignored, ProviderWebhookEventStatus.Ignored, ProviderWebhookEventStatus.Rejected],
            (await EventsAsync()).Select(e => e.Status).Order());
    }

    [Fact]
    public async Task EventClaimedByAStoppedProcessor_IsTakenUpAfterItsClaimExpires()
    {
        _client.Set(Package("pkg-claim", "Created"));
        await AcceptAsync("created", Created("pkg-claim"));
        await using (var db = await _tenants.CreateAsync(_tenantId, Ct))
        {
            await db.ProviderWebhookEvents.ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ProviderWebhookEventStatus.Processing)
                .SetProperty(e => e.LockedUntil, _clock.GetUtcNow().UtcDateTime + TrendyolGoMealWebhookProcessor.ClaimDuration), Ct);
        }

        Assert.Equal(0, await Processor().ProcessDueAsync(10, Ct));
        _clock.Advance(TrendyolGoMealWebhookProcessor.ClaimDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Processor().ProcessDueAsync(10, Ct));

        Assert.Equal("Reconciled", Assert.Single(await EventsAsync()).Outcome);
    }

    [Fact]
    public async Task Inspection_ShowsDeliveryProcessingAndTheMatchedOrder_WithoutThePayload()
    {
        _client.Set(Package("pkg-inspect", "Picking"));
        await AcceptAsync("created", Created("pkg-inspect"));
        await AcceptAsync("created", Created("pkg-inspect"));
        await Processor().ProcessDueAsync(10, Ct);

        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        var view = Assert.Single(await TrendyolGoMealWebhookInspection.ListAsync(db, 20, Ct));

        Assert.Equal("created", view.EventType);
        Assert.Equal("pkg-inspect", view.ExternalOrderId);
        Assert.Equal(2, view.DeliveryCount);
        Assert.Equal(ProviderWebhookEventStatus.Processed, view.Status);
        Assert.Equal("Reconciled", view.Outcome);
        Assert.Equal("946511491", view.OrderCode);
        Assert.Equal(OrderStatus.Accepted, view.OrderStatus);
        Assert.DoesNotContain("Gündoğdu", view.ToString(), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private void Seed(string storeId, string? supplierId, bool orderSyncEnabled, bool replace = false)
    {
        if (replace)
            _tenants.Execute(_tenantId, "DELETE FROM PlatformConnections; DELETE FROM TenantOperationalSettings;");

        _tenants.Seed(_tenantId, db =>
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = orderSyncEnabled,
                OperationalMode = TenantOperationalMode.Live,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = _connectionId,
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
        });
    }

    private async Task AcceptAsync(string eventType, string body) =>
        await new TrendyolGoMealWebhookInbox(Options.Create(_options), _tenants, NullLogger<TrendyolGoMealWebhookInbox>.Instance, _clock)
            .AcceptAsync(eventType, Encoding.UTF8.GetBytes(body), Ct);

    private TrendyolGoMealWebhookProcessor Processor() =>
        new(Options.Create(_options), _tenants, Sync(), new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            NullLogger<TrendyolGoMealWebhookProcessor>.Instance, _clock);

    private OrderSyncService Sync() =>
        new(_tenants, [_client], new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance), _autoApprove, _receipts, _notifier,
            NullLogger<OrderSyncService>.Instance, time: _clock);

    private async Task<List<ProviderWebhookEvent>> EventsAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        return await db.ProviderWebhookEvents.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<Order> SingleOrderAsync(string externalOrderId)
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private async Task<PlatformConnection> ConnectionAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenantId, Ct);
        return await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
    }

    private static string Created(string packageId, string seller = Seller, string store = Store) =>
        TrendyolGoMealWebhookReceiverTests.Created(packageId, seller, store);

    private string Thin(string packageId) =>
        $$"""{ "id": "{{packageId}}", "orderNumber": "946511491", "orderCode": "047", "timestamp": {{_clock.GetUtcNow().ToUnixTimeMilliseconds()}} }""";

    private string Cancellation(string packageId, string eventType, string? status) =>
        $$"""
        { "id": "{{packageId}}", "orderNumber": "946511491", "orderCode": "047",
          {{(status is null ? "" : $"\"status\": \"{status}\",")}}
          "timestamp": {{_clock.GetUtcNow().ToUnixTimeMilliseconds()}},
          "lines": [ { "items": [ { "packageItemId": "1", "lineItemId": 1000004632264 } ] } ],
          "cancelInfo": { "reasonCode": {{(eventType == "unsupplied" ? 621 : 601)}}, "reason": "Synthetic reason" } }
        """;

    private static ExternalOrderDto Package(string packageId, string providerStatus) =>
        new(
            FoodPlatform.TrendyolYemek,
            packageId,
            "946511491",
            DateTime.UtcNow.AddMinutes(-2),
            "Webhook Customer",
            "0000000000",
            "Synthetic Street 1",
            173.93m,
            0m,
            0m,
            173.93m,
            PaymentMethod.OnlinePayment,
            PaymentStatus.Paid,
            providerStatus,
            "{}",
            [new ExternalOrderItemDto(packageId + "-line-1", "Synthetic drink", 1, 173.93m, 173.93m, null, [])])
        {
            ProviderSupplierId = Seller,
            ProviderStoreId = Store
        };

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A Trendyol GO-like client that fetches by window and returns its packages for any window.</summary>
    private sealed class FakeWindowedClient : IFoodPlatformClient
    {
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];
        private Exception? _failure;

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => TimeSpan.FromHours(1);
        public List<OrderFetchWindow> Windows { get; } = [];

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;
        public void FailWith(Exception failure) => _failure = failure;
        public void Succeed() => _failure = null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct)
        {
            Windows.Add(window);
            return _failure is null ? Task.FromResult(_orders) : Task.FromException<IReadOnlyCollection<ExternalOrderDto>>(_failure);
        }

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

        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct)
        {
            _processed.Enqueue(orderId);
            return Task.CompletedTask;
        }
    }
}
