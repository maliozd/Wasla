using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// WAS-18: two Worker passes observe the same new provider package at the same time, on a real SQL Server. Both read
/// that no order has its idempotency key and both insert it; the unique index lets one insert commit and refuses the
/// other. Each pass uses its own service, context and connection pool, as separate Worker processes would. The losing
/// pass must treat the refused insert as an observation of the order the winner stored: no failed connection, no
/// failure counter, no circuit, and the new-order side effects (auto-approve, receipt, live notification) exactly once.
/// </summary>
public sealed class OrderIngestionInsertRaceSqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingReceipts _receipts = new();
    private readonly RecordingAutoApprove _autoApprove = new();

    public OrderIngestionInsertRaceSqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoserPausedBetweenItsReadAndItsInsert_ObservesTheWinnersOrder_WithoutFailingItsConnection()
    {
        var before = await PrepareAsync();
        var payload = Payload($"RACE-HELD-{Guid.NewGuid():N}", "Created");

        // Worker A reads "no such order" and stops right before its INSERT; Worker B stores the order meanwhile.
        var held = new HoldBeforeOrderInsert();
        var loserPass = Sync("worker-a", Provider(payload), held).SyncCustomerWithResultAsync(_tenantId, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        var winner = await Sync("worker-b", Provider(payload)).SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, winner.InsertedCount);

        held.Resume();
        var loser = await loserPass.WaitAsync(Deadline, Ct);

        Assert.Equal(0, loser.FailedConnections);
        Assert.Equal(0, loser.InsertedCount);
        Assert.Equal(0, loser.UpdatedCount);
        Assert.Equal(1, loser.UnchangedCount);

        var order = await SingleOrderAsync(payload.ExternalOrderId);
        Assert.Equal(OrderStatus.New, order.InternalStatus);
        Assert.Equal(OrderChangeVersions.Initial, order.ChangeVersion);
        await AssertConnectionHealthyAsync(before);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
        Assert.Empty(_receipts.Created);
    }

    [Fact]
    public async Task FreeRaceOfTwoFirstObservations_StoresOneOrder_AndRunsItsSideEffectsOnce()
    {
        var before = await PrepareAsync();
        var payload = Payload($"RACE-FREE-{Guid.NewGuid():N}", "Created");

        // Both passes stop at their INSERT after both have read that the order is missing, then go on together.
        var barrier = new OrderInsertBarrier(parties: 2);
        var passes = await Task.WhenAll(
            Sync("worker-a", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct),
            Sync("worker-b", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct))
            .WaitAsync(Deadline, Ct);

        Assert.True(barrier.Released, "The two passes never reached their inserts together.");
        Assert.All(passes, p => Assert.Equal(0, p.FailedConnections));
        Assert.Equal(1, passes.Sum(p => p.InsertedCount));
        Assert.Equal(1, passes.Sum(p => p.UnchangedCount));
        Assert.Equal(0, passes.Sum(p => p.UpdatedCount));

        var order = await SingleOrderAsync(payload.ExternalOrderId);
        await AssertConnectionHealthyAsync(before);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
        Assert.Empty(_receipts.Created);
    }

    [Fact]
    public async Task FreeRaceOfAProviderAcceptedOrder_CreatesOneReceipt()
    {
        var before = await PrepareAsync();
        var payload = Payload($"RACE-ACCEPTED-{Guid.NewGuid():N}", "Picking");

        var barrier = new OrderInsertBarrier(parties: 2);
        var passes = await Task.WhenAll(
            Sync("worker-a", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct),
            Sync("worker-b", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct))
            .WaitAsync(Deadline, Ct);

        Assert.True(barrier.Released, "The two passes never reached their inserts together.");
        Assert.All(passes, p => Assert.Equal(0, p.FailedConnections));
        var order = await SingleOrderAsync(payload.ExternalOrderId);
        Assert.Equal(OrderStatus.Accepted, order.InternalStatus);
        await AssertConnectionHealthyAsync(before);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
    }

    [Fact]
    public async Task LoserCarryingANewerStatus_AppliesItThroughTheLockedUpdate_Once()
    {
        var before = await PrepareAsync();
        var id = $"RACE-NEWER-{Guid.NewGuid():N}";
        var created = Payload(id, "Created");
        var accepted = Payload(id, "Picking");

        var held = new HoldBeforeOrderInsert();
        var loserPass = Sync("worker-a", Provider(accepted), held).SyncCustomerWithResultAsync(_tenantId, Ct);
        await held.Reached.WaitAsync(Deadline, Ct);

        var winner = await Sync("worker-b", Provider(created)).SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, winner.InsertedCount);

        held.Resume();
        var loser = await loserPass.WaitAsync(Deadline, Ct);

        Assert.Equal(0, loser.FailedConnections);
        Assert.Equal(0, loser.InsertedCount);
        Assert.Equal(1, loser.UpdatedCount);
        var order = await SingleOrderAsync(id);
        Assert.Equal(OrderStatus.Accepted, order.InternalStatus);
        Assert.Equal("Picking", order.PlatformStatus);
        await AssertConnectionHealthyAsync(before);
        // The winner inserted it New (auto-approve considered once); the loser's Accepted is a transition: one receipt.
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([OrderChangeVersions.Initial, OrderChangeVersions.Initial + 1], _notifier.VersionsFor(order.Id));
    }

    [Fact]
    public async Task RepeatedDeliveryAfterTheRace_ChangesNothing()
    {
        var before = await PrepareAsync();
        var payload = Payload($"RACE-REPEAT-{Guid.NewGuid():N}", "Picking");

        var barrier = new OrderInsertBarrier(parties: 2);
        await Task.WhenAll(
            Sync("worker-a", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct),
            Sync("worker-b", Provider(payload), barrier).SyncCustomerWithResultAsync(_tenantId, Ct))
            .WaitAsync(Deadline, Ct);
        var afterRace = await SingleOrderAsync(payload.ExternalOrderId);

        var repeat = await Sync("worker-c", Provider(payload)).SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, repeat.FailedConnections);
        Assert.Equal(1, repeat.UnchangedCount);
        var order = await SingleOrderAsync(payload.ExternalOrderId);
        Assert.Equal(afterRace.ChangeVersion, order.ChangeVersion);
        Assert.Equal(afterRace.UpdatedAt, order.UpdatedAt);
        await AssertConnectionHealthyAsync(before);
        Assert.Equal([order.Id], _receipts.Created);
        Assert.Equal([order.Id], _autoApprove.Processed);
        Assert.Equal([OrderChangeVersions.Initial], _notifier.VersionsFor(order.Id));
    }

    [Fact]
    public async Task GenuineDatabaseFailureOnInsert_StillFailsTheConnection_AndALaterPassStoresTheOrder()
    {
        var before = await PrepareAsync();
        var id = $"RACE-TRUNCATE-{Guid.NewGuid():N}";
        // ExternalOrderCode is nvarchar(128): SQL Server refuses this insert with a truncation error, not a duplicate key.
        var tooLong = Payload(id, "Created") with { ExternalOrderCode = new string('X', 129) };

        var failed = await Sync("worker-a", Provider(tooLong)).SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(1, failed.FailedConnections);
        await using (var db = _database.Open())
        {
            Assert.False(await db.Orders.AnyAsync(o => o.ExternalOrderId == id, Ct));
            var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(before.ConsecutiveFailures + 1, connection.ConsecutiveFailures);
            Assert.Equal(before.LastSuccessfulSync, connection.LastSuccessfulSync);
            Assert.Equal(before.IntegrationErrors + 1, await db.IntegrationErrors.CountAsync(Ct));
        }

        Assert.Empty(_autoApprove.Processed);
        Assert.Empty(_notifier.All);

        var retried = await Sync("worker-a", Provider(Payload(id, "Created"))).SyncCustomerWithResultAsync(_tenantId, Ct);

        Assert.Equal(0, retried.FailedConnections);
        Assert.Equal(1, retried.InsertedCount);
        var order = await SingleOrderAsync(id);
        await using (var db = _database.Open())
        {
            var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(0, connection.ConsecutiveFailures);
            Assert.NotNull(connection.LastSuccessfulSync);
            if (before.LastSuccessfulSync is { } previous)
                Assert.True(connection.LastSuccessfulSync > previous, "The checkpoint did not move after the retry.");
        }

        Assert.Equal([order.Id], _autoApprove.Processed);
    }

    [Fact]
    public async Task OnlyADuplicateOrderIdentityCountsAsAConcurrentInsert()
    {
        await PrepareAsync();
        var id = $"RACE-CLASSIFY-{Guid.NewGuid():N}";
        await Sync("seed", Provider(Payload(id, "Created"))).SyncCustomerWithResultAsync(_tenantId, Ct);
        var storedKey = (await SingleOrderAsync(id)).IdempotencyKey;

        // The same Platform + ExternalOrderId again: the order identity index refuses it.
        var duplicateOrder = await SaveFailureAsync(db => db.Orders.Add(new Order
        {
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = id,
            IdempotencyKey = storedKey,
            CustomerName = "Duplicate"
        }));
        Assert.True(OrderIngestion.IsOrderIdentityConflict(duplicateOrder));

        // A duplicate key on another table's unique index is not this order.
        var duplicateConnection = await SaveFailureAsync(db => db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            StoreId = "second-store",
            EncryptedApiKey = "fake-key",
            EncryptedApiSecret = "fake-secret"
        }));
        Assert.False(OrderIngestion.IsOrderIdentityConflict(duplicateConnection));

        // A refused order that is not a duplicate (string truncation) is a genuine failure.
        var truncated = await SaveFailureAsync(db => db.Orders.Add(new Order
        {
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = $"RACE-CLASSIFY-LONG-{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ExternalOrderCode = new string('X', 129),
            CustomerName = "Truncated"
        }));
        Assert.False(OrderIngestion.IsOrderIdentityConflict(truncated));
    }

    private async Task<DbUpdateException> SaveFailureAsync(Action<TenantDbContext> add)
    {
        await using var db = _database.Open("classifier");
        add(db);
        return await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    private sealed record HealthBefore(int ConsecutiveFailures, DateTime? LastSuccessfulSync, int IntegrationErrors);

    /// <summary>Seeds the tenant rows once and records the connection's health before the test.</summary>
    private async Task<HealthBefore> PrepareAsync()
    {
        await using var db = _database.Open();
        if (!await db.TenantOperationalSettings.AnyAsync(s => s.Id == SettingsId, Ct))
        {
            db.TenantOperationalSettings.Add(new TenantOperationalSettings
            {
                Id = SettingsId,
                OrderSyncEnabled = true,
                AutoApproveNewOrders = false,
                AutoPrintReceiptOnAutoApprove = false,
                ReceiptPrintCopyCount = 1,
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
                StoreId = "store-insert-race",
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                SyncIntervalSeconds = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
        // Tests share the class's database and its one connection row: each starts from a healthy connection.
        await db.PlatformConnections.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.ConsecutiveFailures, 0)
            .SetProperty(c => c.CircuitOpenUntil, (DateTime?)null), Ct);
        var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
        return new HealthBefore(connection.ConsecutiveFailures, connection.LastSuccessfulSync, await db.IntegrationErrors.CountAsync(Ct));
    }

    private async Task AssertConnectionHealthyAsync(HealthBefore before)
    {
        await using var db = _database.Open();
        var connection = await db.PlatformConnections.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(0, connection.ConsecutiveFailures);
        Assert.Null(connection.CircuitOpenUntil);
        Assert.Equal(before.IntegrationErrors, await db.IntegrationErrors.CountAsync(Ct));
    }

    private async Task<Order> SingleOrderAsync(string externalOrderId)
    {
        await using var db = _database.Open();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
    }

    private OrderSyncService Sync(string instance, IFoodPlatformClient provider, params IInterceptor[] interceptors) =>
        new(
            new InterceptedFactory(_database.ConnectionString(instance), interceptors),
            [provider],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            _autoApprove,
            _receipts,
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private static FakeProvider Provider(ExternalOrderDto payload) => new(payload);

    private static ExternalOrderDto Payload(string externalOrderId, string providerStatus) =>
        new(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId[^12..],
            DateTime.UtcNow.AddMinutes(-5),
            "Insert Race Customer",
            "5313241245",
            "Test Address",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.CreditCard,
            PaymentStatus.Paid,
            providerStatus,
            "{}",
            [new ExternalOrderItemDto(externalOrderId + "-item", "Lahmacun", 1, 100m, 100m, null, [])],
            null);

    private static bool IsOrderInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO [Orders]", StringComparison.Ordinal);

    /// <summary>Holds one pass once, right before it sends its order INSERT (after it read that the order is missing).</summary>
    private sealed class HoldBeforeOrderInsert : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Reached => _reached.Task;

        public void Resume() => _resume.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsOrderInsert(command) && Interlocked.Exchange(ref _held, 1) == 0)
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    /// <summary>
    /// Lets the order INSERT of each pass go only when every pass has reached its own: each has read that the order is
    /// missing, so both inserts are real first observations and SQL Server alone decides the winner.
    /// </summary>
    private sealed class OrderInsertBarrier(int parties) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool Released => _all.Task.IsCompletedSuccessfully;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (IsOrderInsert(command) && !_all.Task.IsCompleted)
            {
                if (Interlocked.Increment(ref _arrived) >= parties)
                    _all.TrySetResult();
                await _all.Task.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor[] interceptors) : ITenantDbContextFactory
    {
        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            var options = new DbContextOptionsBuilder<TenantDbContext>().UseSqlServer(connectionString);
            if (interceptors.Length > 0)
                options.AddInterceptors(interceptors);
            return Task.FromResult(new TenantDbContext(options.Options));
        }
    }

    /// <summary>Returns its payload on every fetch; lifecycle calls succeed without side effects.</summary>
    private sealed class FakeProvider(ExternalOrderDto payload) : IFoodPlatformClient
    {
        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([payload]);

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

    /// <summary>Records every newly inserted order handed to auto-approve; the real service decides eligibility.</summary>
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
