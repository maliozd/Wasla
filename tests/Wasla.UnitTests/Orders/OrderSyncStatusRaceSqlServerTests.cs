using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.SqlClient;
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
using Wasla.Infrastructure.Services;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-123: Worker sync updating an existing order while an operator action or another Worker process commits a status
/// for the same order, on a real SQL Server. The sync is paused after it read the order and before its update
/// transaction; the other writer commits in that window, from its own context and connection pool. Sync must merge the
/// provider status into the status committed meanwhile, never into the one it read.
/// </summary>
public sealed class OrderSyncStatusRaceSqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly FakeProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingReceipts _receipts = new();

    public OrderSyncStatusRaceSqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OperatorRejectCommittedDuringSync_IsNotOverwrittenByTheProviderAcceptedStatus()
    {
        var (orderId, payload) = await InsertThroughSyncAsync("REJECT");
        var accepted = payload with { ExternalStatus = "Picking" };

        var paused = new PauseBeforeUpdateTransaction(payload.ExternalOrderId);
        var probe = new ProbeRowLockAfterLockWrite(_database, orderId);
        var sync = Sync("worker", paused, probe);
        _provider.Set(accepted);
        var pass = sync.SyncCustomerWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        // Sync read the order as New and merged the provider's Accepted. The operator rejects it now, so the order's
        // UpdatedAt is later than the time sync took before it paused.
        var rejected = await Operator("operator").TryRejectAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(true, "Orders.RejectSuccess"), rejected);
        var byOperator = await ReadAsync(orderId);

        paused.Resume();
        var result = await pass.WaitAsync(Deadline, Ct);

        Assert.Equal(0, result.FailedConnections);
        // The lock write kept the operator's newer UpdatedAt, and still held the row until sync committed.
        Assert.True(probe.Probed, "The sync never wrote the row lock.");
        Assert.True(probe.Blocked, "Another connection could update the order while sync held its row lock.");
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.NotNull(after.CancelledAt);
        Assert.Null(after.AcceptedAt);
        Assert.Equal("Picking", after.PlatformStatus);
        Assert.True(after.UpdatedAt >= byOperator.UpdatedAt, $"UpdatedAt moved backwards: {byOperator.UpdatedAt:O} -> {after.UpdatedAt:O}.");
        var operatorVersion = byOperator.ChangeVersion;
        Assert.Equal(operatorVersion + 1, after.ChangeVersion);
        Assert.Equal([operatorVersion, after.ChangeVersion], _notifier.VersionsFor(orderId));
        Assert.Empty(_receipts.Created);
    }

    [Fact]
    public async Task OperatorApproveCommittedDuringSync_WhoseMergeKeptNew_StaysAccepted()
    {
        var (orderId, payload) = await InsertThroughSyncAsync("APPROVE");
        var changedTotal = payload with { Total = 150m };

        var paused = new PauseBeforeUpdateTransaction(payload.ExternalOrderId);
        var sync = Sync("worker", paused);
        _provider.Set(changedTotal);
        var pass = sync.SyncCustomerWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        var approved = await Operator("operator").TryApproveAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), approved);
        var byOperator = await ReadAsync(orderId);
        var operatorVersion = byOperator.ChangeVersion;

        paused.Resume();
        var result = await pass.WaitAsync(Deadline, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.NotNull(after.AcceptedAt);
        Assert.Null(after.CancelledAt);
        Assert.Equal(150m, after.TotalAmount);
        Assert.True(after.UpdatedAt >= byOperator.UpdatedAt, $"UpdatedAt moved backwards: {byOperator.UpdatedAt:O} -> {after.UpdatedAt:O}.");
        Assert.Equal(operatorVersion + 1, after.ChangeVersion);
        Assert.Empty(_receipts.Created);
    }

    [Fact]
    public async Task OperatorApproveCommittedDuringAnItemOnlySync_UpdatedAtDoesNotMoveBackwards()
    {
        var (orderId, payload) = await InsertThroughSyncAsync("ITEMS-ONLY");
        // Only the lines change: the order's own columns stay as stored, so its UpdatedAt is whatever the row lock left.
        var changedItems = payload with
        {
            Items = [new ExternalOrderItemDto(payload.ExternalOrderId + "-item", "Lahmacun", 1, 100m, 100m, "No onions", [])]
        };

        var paused = new PauseBeforeUpdateTransaction(payload.ExternalOrderId);
        var sync = Sync("worker", paused);
        _provider.Set(changedItems);
        var pass = sync.SyncCustomerWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        // The sync took its time before it paused; the operator's write is later.
        var approved = await Operator("operator").TryApproveAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), approved);
        var byOperator = await ReadAsync(orderId);

        paused.Resume();
        var result = await pass.WaitAsync(Deadline, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(1, result.UpdatedCount);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Equal(byOperator.AcceptedAt, after.AcceptedAt);
        Assert.Equal(byOperator.ChangeVersion + 1, after.ChangeVersion);
        Assert.True(after.UpdatedAt >= byOperator.UpdatedAt, $"UpdatedAt moved backwards: {byOperator.UpdatedAt:O} -> {after.UpdatedAt:O}.");
        await using var db = _database.Open();
        Assert.Equal("No onions", await db.OrderItems.Where(i => i.OrderId == orderId).Select(i => i.Notes).SingleAsync(Ct));
    }

    [Fact]
    public async Task AnotherWorkerAppliedTheSamePayloadDuringSync_TheSecondWriteChangesNothing()
    {
        var (orderId, payload) = await InsertThroughSyncAsync("TWO-WORKERS");
        var accepted = payload with { ExternalStatus = "Picking" };
        _provider.Set(accepted);

        var paused = new PauseBeforeUpdateTransaction(payload.ExternalOrderId);
        var first = Sync("worker-a", paused).SyncCustomerWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        // A second Worker process applies the same provider status first.
        var second = await Sync("worker-b").SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, second.UpdatedCount);
        var secondVersion = (await ReadAsync(orderId)).ChangeVersion;
        Assert.Equal([orderId], _receipts.Created);

        paused.Resume();
        var result = await first.WaitAsync(Deadline, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.UnchangedCount);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Equal(secondVersion, after.ChangeVersion);
        Assert.Equal([secondVersion], _notifier.VersionsFor(orderId));
        Assert.Equal([orderId], _receipts.Created);
    }

    [Fact]
    public async Task AnotherWorkerAppliedTheSameItemChangesDuringSync_TheSecondWriteChangesNothing()
    {
        var (orderId, payload) = await InsertThroughSyncAsync("TWO-WORKERS-ITEMS");
        var changed = payload with
        {
            ExternalStatus = "Picking",
            Subtotal = 145m,
            Total = 145m,
            Items =
            [
                new ExternalOrderItemDto(payload.ExternalOrderId + "-item", "Lahmacun", 2, 60m, 120m, "No onions",
                    [new ExternalOrderItemOptionDto("Cheese", 5m), new ExternalOrderItemOptionDto("Spicy", 0m)]),
                new ExternalOrderItemDto(payload.ExternalOrderId + "-drink", "Ayran", 1, 20m, 20m, null, [])
            ]
        };
        _provider.Set(changed);

        var paused = new PauseBeforeUpdateTransaction(payload.ExternalOrderId);
        var first = Sync("worker-a", paused).SyncCustomerWithResultAsync(_tenantId, Ct);
        await paused.Reached.WaitAsync(Deadline, Ct);

        // A second Worker process applies the same items, options and status first.
        var second = await Sync("worker-b").SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, second.UpdatedCount);
        var bySecond = await ReadAsync(orderId);
        var secondRows = await ReadItemRowIdsAsync(orderId);
        Assert.Equal(2, secondRows.Items.Length);
        Assert.Equal(2, secondRows.Options.Length);
        Assert.Equal([orderId], _receipts.Created);

        paused.Resume();
        var result = await first.WaitAsync(Deadline, Ct);

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.UnchangedCount);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Equal(bySecond.ChangeVersion, after.ChangeVersion);
        Assert.Equal(bySecond.UpdatedAt, after.UpdatedAt);
        Assert.Equal(bySecond.AcceptedAt, after.AcceptedAt);
        // The item and option rows the second Worker wrote are still there: nothing was deleted and inserted again.
        var afterRows = await ReadItemRowIdsAsync(orderId);
        Assert.Equal(secondRows.Items, afterRows.Items);
        Assert.Equal(secondRows.Options, afterRows.Options);
        Assert.Equal([bySecond.ChangeVersion], _notifier.VersionsFor(orderId));
        Assert.Equal([orderId], _receipts.Created);
    }

    private async Task<(Guid[] Items, Guid[] Options)> ReadItemRowIdsAsync(Guid orderId)
    {
        await using var db = _database.Open();
        var items = await db.OrderItems.AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .Select(i => i.Id)
            .ToListAsync(Ct);
        var options = await db.OrderItemOptions.AsNoTracking()
            .Where(o => items.Contains(o.OrderItemId))
            .Select(o => o.Id)
            .ToListAsync(Ct);
        return (items.Order().ToArray(), options.Order().ToArray());
    }

    /// <summary>Inserts a New order through a normal sync pass, as the Worker would, and forgets its side effects.</summary>
    private async Task<(Guid OrderId, ExternalOrderDto Payload)> InsertThroughSyncAsync(string name)
    {
        await EnsureTenantRowsAsync();
        var payload = Payload($"SYNC-RACE-{name}-{Guid.NewGuid():N}");
        _provider.Set(payload);
        var inserted = await Sync("seed").SyncCustomerWithResultAsync(_tenantId, Ct);
        Assert.Equal(1, inserted.InsertedCount);

        await using var db = _database.Open();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.ExternalOrderId == payload.ExternalOrderId, Ct);
        Assert.Equal(OrderStatus.New, order.InternalStatus);
        _notifier.Reset();
        _receipts.Reset();
        return (order.Id, payload);
    }

    private OrderSyncService Sync(string instance, params IInterceptor[] interceptors) =>
        new(
            new InterceptedFactory(_database.ConnectionString(instance), interceptors),
            [_provider],
            new DefaultOrderStatusMapper(NullLogger<DefaultOrderStatusMapper>.Instance),
            new NoAutoApprove(),
            _receipts,
            _notifier,
            NullLogger<OrderSyncService>.Instance);

    private OrderActionService Operator(string instance) =>
        new(_database.Instance(instance), [_provider], _notifier, NullLogger<OrderActionService>.Instance);

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var db = _database.Open();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task EnsureTenantRowsAsync()
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
                StoreId = "store-sync-race",
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

    private static ExternalOrderDto Payload(string externalOrderId) =>
        new(
            FoodPlatform.TrendyolYemek,
            externalOrderId,
            "TY-" + externalOrderId[^12..],
            DateTime.UtcNow.AddMinutes(-5),
            "Sync Race Customer",
            "5313241245",
            "Test Address",
            100m,
            0m,
            0m,
            100m,
            PaymentMethod.CreditCard,
            PaymentStatus.Paid,
            "Created",
            "{}",
            [new ExternalOrderItemDto(externalOrderId + "-item", "Lahmacun", 1, 100m, 100m, null, [])],
            null);

    /// <summary>
    /// Holds the sync's context once, when it is about to begin a transaction while it tracks the target order: after
    /// the order was read and before the update writes anything.
    /// </summary>
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

    /// <summary>
    /// Right after the sync's row-lock write (its first set-based update of the order inside a transaction), tries to
    /// update the same order from another connection with a short lock timeout. The row lock must hold it off even when
    /// the lock write left the stored values unchanged.
    /// </summary>
    private sealed class ProbeRowLockAfterLockWrite(SqlServerTenantDatabase database, Guid orderId) : DbCommandInterceptor
    {
        private int _probed;

        public bool Probed => Volatile.Read(ref _probed) == 1;
        public bool Blocked { get; private set; }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            var isLockWrite = command.Transaction is not null
                && command.CommandText.Contains("FROM [Orders] AS [o]", StringComparison.Ordinal)
                && command.CommandText.Contains("[UpdatedAt]", StringComparison.Ordinal);
            if (isLockWrite && Interlocked.Exchange(ref _probed, 1) == 0)
                Blocked = await TryUpdateFromAnotherConnectionTimesOutAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }

        private async Task<bool> TryUpdateFromAnotherConnectionTimesOutAsync(CancellationToken ct)
        {
            await using var other = new SqlConnection(database.ConnectionString("lock-probe"));
            await other.OpenAsync(ct).ConfigureAwait(false);
            await using var probe = other.CreateCommand();
            probe.CommandText = "SET LOCK_TIMEOUT 500; UPDATE [Orders] SET [UpdatedAt] = [UpdatedAt] WHERE [Id] = @id;";
            probe.Parameters.AddWithValue("@id", orderId);
            try
            {
                await probe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return false;
            }
            catch (SqlException ex) when (ex.Number == 1222)
            {
                return true;
            }
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

    /// <summary>Returns the set payload on every fetch; lifecycle calls succeed without side effects.</summary>
    private sealed class FakeProvider : IFoodPlatformClient
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

    private sealed class NoAutoApprove : IOrderAutoApproveService
    {
        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
