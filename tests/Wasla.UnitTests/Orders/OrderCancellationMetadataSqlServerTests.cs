using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-200 on a real SQL Server: the status and how the order ended must agree when operators and Worker sync contend.
/// Every caller has its own service, context and connection pool. The provider call is held to make the window where
/// sync reports the provider's echo deterministic; sync is held between its read and its update for the reverse.
/// </summary>
public sealed class OrderCancellationMetadataSqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private const string InvalidStatus = "Orders.InvalidStatusForAction";
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();

    public OrderCancellationMetadataSqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SimultaneousRejectsWithDifferentReasons_StoreTheWinnersReason()
    {
        int[] reasons = [621, 622, 623, 627];
        for (var round = 0; round < 5; round++)
        {
            var orderId = await SeedOrderAsync();
            var before = await ReadAsync(orderId);
            _provider.Reset(delay: TimeSpan.FromMilliseconds(100));
            _notifier.Reset();

            var results = await RaceAsync(reasons.Length, i => Operator($"reject-{i}").TryRejectAsync(_tenantId, orderId, reasons[i], Ct));

            Assert.Equal(new OrderActionResult(true, "Orders.RejectSuccess"), Assert.Single(results, r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(new OrderActionResult(false, InvalidStatus), r));
            var sentToProvider = Assert.Single(_provider.RejectReasonsFor(orderId));
            var after = await ReadAsync(orderId);
            Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
            Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
            Assert.Equal(FoodPlatform.TrendyolYemek, after.CancellationReasonPlatform);
            Assert.Equal(sentToProvider, after.CancellationReasonCode);
            Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
            Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
        }
    }

    [Fact]
    public async Task ApproveRacingReject_LeavesMetadataOnlyOnARejection()
    {
        for (var round = 0; round < 6; round++)
        {
            var orderId = await SeedOrderAsync();
            _provider.Reset(delay: TimeSpan.FromMilliseconds(100));

            var results = await RaceAsync(4, i => i % 2 == 0
                ? Operator($"approve-{i}").TryApproveAsync(_tenantId, orderId, Ct)
                : Operator($"reject-{i}").TryRejectAsync(_tenantId, orderId, 623, Ct));

            var winner = Assert.Single(results, r => r.Succeeded);
            var after = await ReadAsync(orderId);
            if (winner.MessageKey == "Orders.RejectSuccess")
            {
                Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
                Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
                Assert.Equal(623, after.CancellationReasonCode);
            }
            else
            {
                Assert.Equal(OrderStatus.Preparing, after.InternalStatus);
                Assert.Null(after.CancellationOrigin);
                Assert.Null(after.CancellationReasonPlatform);
                Assert.Null(after.CancellationReasonCode);
            }
        }
    }

    [Fact]
    public async Task ProviderRefusalDuringAContestedReject_StoresNothing()
    {
        var orderId = await SeedOrderAsync();
        var before = await ReadAsync(orderId);
        _provider.Reset(failCalls: 1, holdFirstCall: true);
        _notifier.Reset();

        var first = Operator("first").TryRejectAsync(_tenantId, orderId, 621, Ct);
        await _provider.FirstCallEntered.WaitAsync(Deadline, Ct);
        var during = await Operator("during").TryRejectAsync(_tenantId, orderId, 627, Ct);
        _provider.ReleaseHeldCall();

        Assert.Equal(new OrderActionResult(false, InvalidStatus), during);
        Assert.Equal(new OrderActionResult(false, "Orders.RejectFailed"), await first);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.New, after.InternalStatus);
        Assert.Null(after.CancelledAt);
        Assert.Null(after.CancellationOrigin);
        Assert.Null(after.CancellationReasonPlatform);
        Assert.Null(after.CancellationReasonCode);
        Assert.Equal(before.ChangeVersion, after.ChangeVersion);
        Assert.Empty(_notifier.VersionsFor(orderId));
    }

    [Fact]
    public async Task TheProvidersEchoStoredDuringTheRejectCall_GivesWayToWhatTheOperatorChose()
    {
        var orderId = await SeedOrderAsync();
        _provider.Reset(holdFirstCall: true);
        _notifier.Reset();

        var action = Operator("operator").TryRejectAsync(_tenantId, orderId, 622, Ct);
        await _provider.FirstCallEntered.WaitAsync(Deadline, Ct);

        // Trendyol GO has applied the rejection and a sync pass stores it before the response reaches the action.
        _provider.Set(PackageOf(await ReadAsync(orderId), "UnSupplied") with { ProviderCancellationReasonCode = 621 });
        Assert.Equal(1, (await Sync("worker").SyncTenantWithResultAsync(_tenantId, Ct)).UpdatedCount);
        var bySync = await ReadAsync(orderId);
        Assert.Equal(OrderCancellationOrigin.RestaurantOnPlatform, bySync.CancellationOrigin);
        Assert.Equal(621, bySync.CancellationReasonCode);

        _provider.ReleaseHeldCall();

        // The conditional status write lost, and the answer stays what it was before WAS-200.
        Assert.Equal(new OrderActionResult(false, InvalidStatus), await action);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal(bySync.CancelledAt, after.CancelledAt);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Equal(FoodPlatform.TrendyolYemek, after.CancellationReasonPlatform);
        Assert.Equal(622, after.CancellationReasonCode);
        Assert.Equal(bySync.ChangeVersion + 1, after.ChangeVersion);
        Assert.True(after.UpdatedAt >= bySync.UpdatedAt, "UpdatedAt moved backwards.");
    }

    [Fact]
    public async Task ACancellationNotFromTheRestaurant_StoredDuringTheRejectCall_IsNotReplaced()
    {
        var orderId = await SeedOrderAsync();
        _provider.Reset(holdFirstCall: true);
        _notifier.Reset();

        var action = Operator("operator").TryRejectAsync(_tenantId, orderId, 622, Ct);
        await _provider.FirstCallEntered.WaitAsync(Deadline, Ct);

        _provider.Set(PackageOf(await ReadAsync(orderId), "Cancelled"));
        await Sync("worker").SyncTenantWithResultAsync(_tenantId, Ct);
        var bySync = await ReadAsync(orderId);
        _provider.ReleaseHeldCall();

        Assert.Equal(new OrderActionResult(false, InvalidStatus), await action);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderCancellationOrigin.ProviderOrCustomer, after.CancellationOrigin);
        Assert.Null(after.CancellationReasonPlatform);
        Assert.Null(after.CancellationReasonCode);
        Assert.Equal(bySync.ChangeVersion, after.ChangeVersion);
    }

    [Fact]
    public async Task AnOrderDeliveredDuringTheRejectCall_GetsNoMetadata()
    {
        var orderId = await SeedOrderAsync();
        _provider.Reset(holdFirstCall: true);

        var action = Operator("operator").TryRejectAsync(_tenantId, orderId, 622, Ct);
        await _provider.FirstCallEntered.WaitAsync(Deadline, Ct);
        _provider.Set(PackageOf(await ReadAsync(orderId), "Delivered"));
        await Sync("worker").SyncTenantWithResultAsync(_tenantId, Ct);
        _provider.ReleaseHeldCall();

        Assert.Equal(new OrderActionResult(false, InvalidStatus), await action);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Delivered, after.InternalStatus);
        Assert.Null(after.CancellationOrigin);
        Assert.Null(after.CancellationReasonCode);
    }

    [Fact]
    public async Task OperatorRejectCommittedWhileSyncIsBetweenItsReadAndItsWrite_KeepsTheOperatorsMetadata()
    {
        var orderId = await SeedOrderAsync();
        var package = PackageOf(await ReadAsync(orderId), "UnSupplied");
        _provider.Reset();

        var paused = new PauseBeforeUpdateTransaction(package.ExternalOrderId);
        _provider.Set(package);
        var pass = Sync("worker", paused).SyncTenantWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        // Sync has read the order as New and is about to record the provider's report as the restaurant's own.
        var rejected = await Operator("operator").TryRejectAsync(_tenantId, orderId, 627, Ct);
        Assert.Equal(new OrderActionResult(true, "Orders.RejectSuccess"), rejected);

        paused.Resume();
        Assert.Equal(0, (await pass.WaitAsync(Deadline, Ct)).FailedConnections);

        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Equal("UnSupplied", after.PlatformStatus);
        Assert.Equal(OrderCancellationOrigin.OperatorRejected, after.CancellationOrigin);
        Assert.Equal(FoodPlatform.TrendyolYemek, after.CancellationReasonPlatform);
        Assert.Equal(627, after.CancellationReasonCode);
    }

    private OrderActionService Operator(string instance) =>
        new(_database.Instance(instance), [_provider], _notifier, NullLogger<OrderActionService>.Instance);

    private OrderSyncService Sync(string instance, params IInterceptor[] interceptors) =>
        new(
            new InterceptedFactory(_database.ConnectionString(instance), interceptors),
            [_provider],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            new NoReceipts(),
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private static async Task<OrderActionResult[]> RaceAsync(int callers, Func<int, Task<OrderActionResult>> call)
    {
        using var start = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers)
            .Select(i => Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                return await call(i);
            }, Ct))
            .ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var db = _database.Open();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task<Guid> SeedOrderAsync()
    {
        await using var db = _database.Open();
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

        if (!await db.PlatformConnections.AnyAsync(c => c.Platform == FoodPlatform.TrendyolYemek, Ct))
        {
            db.PlatformConnections.Add(new PlatformConnection
            {
                Id = Guid.NewGuid(),
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = "store-cancellation",
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                SyncIntervalSeconds = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var orderId = Guid.NewGuid();
        var externalId = $"CANCEL-{orderId:N}";
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalId,
            ExternalOrderCode = "TY-" + externalId[^12..],
            IdempotencyKey = OrderIngestion.IdempotencyKey(FoodPlatform.TrendyolYemek, externalId),
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Cancellation Customer",
            CustomerPhone = "5000000000",
            CustomerAddress = "Synthetic Street 1",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            ReceivedAt = DateTime.UtcNow.AddMinutes(-5),
            RawPayloadJson = "{}",
            ChangeVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    /// <summary>The order as the provider reports it with another status; only the status differs from what is stored.</summary>
    private static ExternalOrderDto PackageOf(Order order, string providerStatus) =>
        new(
            order.Platform,
            order.ExternalOrderId,
            order.ExternalOrderCode,
            null,
            order.CustomerName,
            order.CustomerPhone,
            order.CustomerAddress,
            order.TotalAmount,
            order.DeliveryFee,
            order.ServiceFee,
            order.TotalAmount,
            order.PaymentMethod,
            order.PaymentStatus,
            providerStatus,
            "{}",
            []);

    private sealed class PauseBeforeUpdateTransaction(string externalOrderId) : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            var tracksOrder = eventData.Context?.ChangeTracker.Entries<Order>()
                .Any(e => e.Entity.ExternalOrderId == externalOrderId) == true;
            if (tracksOrder && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor[] interceptors) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString);
            if (interceptors.Length > 0)
                options.AddInterceptors(interceptors);
            return Task.FromResult(new TenantDbContext(options.Options));
        }
    }

    /// <summary>Records the reason each reject was sent with; can delay, fail or hold the lifecycle calls.</summary>
    private sealed class FakeProvider : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<(string ExternalOrderId, int? ReasonId)> _rejects = new();
        private IReadOnlyCollection<ExternalOrderDto> _orders = [];
        private TimeSpan _delay;
        private int _failCalls;
        private bool _hold;
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;
        public Task FirstCallEntered => _entered.Task;

        public void Reset(TimeSpan delay = default, int failCalls = 0, bool holdFirstCall = false)
        {
            _rejects.Clear();
            _orders = [];
            _delay = delay;
            _failCalls = failCalls;
            _hold = holdFirstCall;
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Set(params ExternalOrderDto[] orders) => _orders = orders;
        public void ReleaseHeldCall() => _release.TrySetResult();

        public IReadOnlyList<int?> RejectReasonsFor(Guid orderId) =>
            _rejects.Where(r => r.ExternalOrderId == $"CANCEL-{orderId:N}").Select(r => r.ReasonId).ToList();

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult(_orders);

        public async Task RejectOrderAsync(PlatformConnection connection, Order order, int? reasonId, CancellationToken ct)
        {
            _rejects.Enqueue((order.ExternalOrderId, reasonId));
            await EnterAsync();
        }

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => EnterAsync();
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Task.CompletedTask;

        private async Task EnterAsync()
        {
            _entered.TrySetResult();
            if (_hold)
                await _release.Task.ConfigureAwait(false);
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay).ConfigureAwait(false);
            if (Interlocked.Decrement(ref _failCalls) >= 0)
                throw new HttpRequestException("provider refused");
        }
    }

    private sealed class RecordingNotifier : ITenantChangeNotifier
    {
        private readonly ConcurrentQueue<TenantChange> _changes = new();

        public void Reset() => _changes.Clear();

        public IReadOnlyList<long> VersionsFor(Guid orderId) =>
            _changes.OfType<OrderChanged>().Where(c => c.OrderId == orderId).Select(c => c.Version).ToList();

        public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class NoReceipts : IOrderReceiptCreationService
    {
        public Task TryCreateOnOrderAcceptedAsync(Guid tenantId, Guid orderId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid tenantId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) => Task.CompletedTask;
    }
}
