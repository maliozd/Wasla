using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-104: concurrent order lifecycle actions on a real SQL Server. Every caller uses its own service, DbContext and
/// connection pool, as separate Web or Worker processes would. A contested transition must have exactly one winner, one
/// provider call, one ChangeVersion raise and one notification; every loser gets Orders.InvalidStatusForAction without
/// reaching the provider.
/// </summary>
public sealed class OrderActionConcurrencySqlServerTests : IClassFixture<SqlServerTenantDatabase>
{
    private const string InvalidStatus = "Orders.InvalidStatusForAction";
    private static readonly Guid SettingsId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly TimeSpan LoserDeadline = TimeSpan.FromSeconds(15);

    private readonly SqlServerTenantDatabase _database;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly RecordingProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();

    public OrderActionConcurrencySqlServerTests(SqlServerTenantDatabase database)
    {
        _database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SimultaneousApprovals_FromSeparateInstances_HaveOneWinner()
    {
        for (var round = 0; round < 5; round++)
        {
            var orderId = await SeedOrderAsync(OrderStatus.New);
            var before = await ReadAsync(orderId);
            _provider.Reset(delay: TimeSpan.FromMilliseconds(150));
            _notifier.Reset();

            var results = await RaceAsync(6, i => Instance($"approve-{i}").TryApproveAsync(_tenantId, orderId, Ct));

            AssertSingleWinner(results, "Orders.ApproveSuccess");
            Assert.Equal(["accept"], _provider.CallsFor(orderId));
            var after = await ReadAsync(orderId);
            Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
            Assert.NotNull(after.AcceptedAt);
            Assert.Null(after.CancelledAt);
            Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
            Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
        }
    }

    [Fact]
    public async Task ApproveRacingReject_LeavesOneCoherentOutcome()
    {
        for (var round = 0; round < 5; round++)
        {
            var orderId = await SeedOrderAsync(OrderStatus.New);
            var before = await ReadAsync(orderId);
            _provider.Reset(delay: TimeSpan.FromMilliseconds(150));
            _notifier.Reset();

            var results = await RaceAsync(6, i => i % 2 == 0
                ? Instance($"approve-{i}").TryApproveAsync(_tenantId, orderId, Ct)
                : Instance($"reject-{i}").TryRejectAsync(_tenantId, orderId, Ct));

            var winner = Assert.Single(results, r => r.Succeeded);
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(InvalidStatus, r.MessageKey));
            var after = await ReadAsync(orderId);
            if (winner.MessageKey == "Orders.ApproveSuccess")
            {
                Assert.Equal(["accept"], _provider.CallsFor(orderId));
                Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
                Assert.NotNull(after.AcceptedAt);
                Assert.Null(after.CancelledAt);
            }
            else
            {
                Assert.Equal(["reject"], _provider.CallsFor(orderId));
                Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
                Assert.NotNull(after.CancelledAt);
                Assert.Null(after.AcceptedAt);
            }

            Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
            Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
        }
    }

    [Fact]
    public async Task WhileTheWinnerIsAtTheProvider_IdenticalAndConflictingCallersAreRefusedWithoutAProviderCall()
    {
        var orderId = await SeedOrderAsync(OrderStatus.New);
        var before = await ReadAsync(orderId);
        _provider.Reset(holdFirstCall: true);
        _notifier.Reset();

        var winner = Instance("winner").TryApproveAsync(_tenantId, orderId, Ct);
        await _provider.FirstCallEntered.WaitAsync(LoserDeadline, Ct);

        // The winner is inside its provider call. Every other caller, identical or conflicting, must finish now
        // without reaching the provider; without a claim they would join the provider call and never return here.
        var losers = Task.WhenAll(
            Instance("same-1").TryApproveAsync(_tenantId, orderId, Ct),
            Instance("same-2").TryApproveAsync(_tenantId, orderId, Ct),
            Instance("conflict-1").TryRejectAsync(_tenantId, orderId, Ct),
            Instance("conflict-2").TryRejectAsync(_tenantId, orderId, Ct));
        var finished = await Task.WhenAny(losers, Task.Delay(LoserDeadline, Ct));
        _provider.ReleaseHeldCall();

        Assert.True(finished == losers, "A competing caller was not refused while the winner held the transition.");
        Assert.All(await losers, r => Assert.Equal(new OrderActionResult(false, InvalidStatus), r));
        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), await winner);
        Assert.Equal(["accept"], _provider.CallsFor(orderId));
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Null(after.CancelledAt);
        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
    }

    [Fact]
    public async Task OperatorRacingWorkerAutoApprove_CallsTheProviderOnce()
    {
        await EnableAutoApproveAsync();
        for (var round = 0; round < 4; round++)
        {
            var orderId = await SeedOrderAsync(OrderStatus.New);
            var before = await ReadAsync(orderId);
            _provider.Reset(delay: TimeSpan.FromMilliseconds(150));
            _notifier.Reset();
            var receipts = new RecordingReceipts();
            var worker = new OrderAutoApproveService(
                _database.Instance("worker"),
                Instance("worker"),
                receipts,
                NullLogger<OrderAutoApproveService>.Instance);
            var operatorRejects = round % 2 == 1;

            var operatorResults = new ConcurrentBag<OrderActionResult>();
            await RaceAsync(4, async i =>
            {
                if (i == 0)
                {
                    await worker.ProcessNewlyInsertedOrderAsync(_tenantId, orderId, OrderStatus.New, Ct);
                    return new OrderActionResult(true, "worker");
                }

                var result = operatorRejects
                    ? await Instance($"operator-{i}").TryRejectAsync(_tenantId, orderId, Ct)
                    : await Instance($"operator-{i}").TryApproveAsync(_tenantId, orderId, Ct);
                operatorResults.Add(result);
                return result;
            });

            var calls = _provider.CallsFor(orderId);
            Assert.Single(calls);
            var after = await ReadAsync(orderId);
            var operatorWon = operatorResults.Count(r => r.Succeeded);
            Assert.InRange(operatorWon, 0, 1);
            Assert.All(operatorResults.Where(r => !r.Succeeded), r => Assert.Equal(InvalidStatus, r.MessageKey));
            if (operatorWon == 0)
            {
                // The Worker won: it approved and created the receipt; no operator reached the provider.
                Assert.Equal(["accept"], calls);
                Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
                Assert.Equal([orderId], receipts.Created);
            }
            else
            {
                Assert.Empty(receipts.Created);
                Assert.Equal(operatorRejects ? OrderStatus.Cancelled : OrderStatus.Accepted, after.InternalStatus);
            }

            Assert.False(after.AcceptedAt is not null && after.CancelledAt is not null, "Both AcceptedAt and CancelledAt are set.");
            Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
            Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
        }
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, OrderStatus.Preparing, null)]
    [InlineData(OrderStatus.Preparing, OrderStatus.ReadyForPickup, "invoiced")]
    public async Task SimultaneousLifecycleActions_HaveOneWinner(OrderStatus from, OrderStatus to, string? providerAction)
    {
        for (var round = 0; round < 4; round++)
        {
            var orderId = await SeedOrderAsync(from);
            var before = await ReadAsync(orderId);
            _provider.Reset(delay: TimeSpan.FromMilliseconds(150));
            _notifier.Reset();

            var results = await RaceAsync(6, i => to == OrderStatus.Preparing
                ? Instance($"lifecycle-{i}").MarkPreparingAsync(_tenantId, orderId, Ct)
                : Instance($"lifecycle-{i}").MarkReadyForPickupAsync(_tenantId, orderId, Ct));

            AssertSingleWinner(results, to == OrderStatus.Preparing ? "Orders.StartPreparingSuccess" : "Orders.MarkReadySuccess");
            Assert.Equal(providerAction is null ? [] : [providerAction], _provider.CallsFor(orderId));
            var after = await ReadAsync(orderId);
            Assert.Equal(to, after.InternalStatus);
            Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
            Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));
        }
    }

    [Fact]
    public async Task ProviderFailure_ChangesNothing_AndARetrySucceedsOnce()
    {
        var orderId = await SeedOrderAsync(OrderStatus.New);
        var before = await ReadAsync(orderId);
        _provider.Reset(failCalls: 1, holdFirstCall: true);
        _notifier.Reset();

        // The first caller reaches the provider, which fails after a while; callers arriving meanwhile are refused.
        var first = Instance("first").TryApproveAsync(_tenantId, orderId, Ct);
        await _provider.FirstCallEntered.WaitAsync(LoserDeadline, Ct);
        var during = Task.WhenAll(
            Instance("during-1").TryApproveAsync(_tenantId, orderId, Ct),
            Instance("during-2").TryRejectAsync(_tenantId, orderId, Ct));
        var finished = await Task.WhenAny(during, Task.Delay(LoserDeadline, Ct));
        _provider.ReleaseHeldCall();

        Assert.True(finished == during, "A competing caller was not refused while the failing call held the transition.");
        Assert.All(await during, r => Assert.Equal(new OrderActionResult(false, InvalidStatus), r));
        Assert.Equal(new OrderActionResult(false, "Orders.ApproveFailed"), await first);
        Assert.Equal(["accept"], _provider.CallsFor(orderId));
        var unchanged = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.New, unchanged.InternalStatus);
        Assert.Null(unchanged.AcceptedAt);
        Assert.Equal(before.ChangeVersion, unchanged.ChangeVersion);
        Assert.Empty(_notifier.VersionsFor(orderId));

        // The failure released the claim: a retry is a new attempt and succeeds once.
        var retry = await Instance("retry").TryApproveAsync(_tenantId, orderId, Ct);

        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), retry);
        var callsAfterFailure = 1;
        Assert.Equal(callsAfterFailure + 1, _provider.CallsFor(orderId).Count);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Accepted, after.InternalStatus);
        Assert.Equal(before.ChangeVersion + 1, after.ChangeVersion);
        Assert.Equal([after.ChangeVersion], _notifier.VersionsFor(orderId));

        var repeated = await Instance("repeat").TryApproveAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(false, InvalidStatus), repeated);
        Assert.Equal(callsAfterFailure + 1, _provider.CallsFor(orderId).Count);
    }

    [Fact]
    public async Task StatusChangedBySyncDuringTheProviderCall_IsNotOverwritten()
    {
        var orderId = await SeedOrderAsync(OrderStatus.New);
        _provider.Reset(holdFirstCall: true);
        _notifier.Reset();

        var action = Instance("operator").TryApproveAsync(_tenantId, orderId, Ct);
        await _provider.FirstCallEntered.WaitAsync(LoserDeadline, Ct);

        // Worker sync applies the platform's cancellation while the operator's accept call is in flight.
        await using (var sync = _database.Open("worker-sync"))
        {
            await sync.Orders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InternalStatus, OrderStatus.Cancelled)
                .SetProperty(o => o.CancelledAt, DateTime.UtcNow)
                .SetProperty(o => o.ChangeVersion, o => o.ChangeVersion + 1), Ct);
        }

        var syncVersion = (await ReadAsync(orderId)).ChangeVersion;
        _provider.ReleaseHeldCall();
        var result = await action;

        Assert.Equal(new OrderActionResult(false, InvalidStatus), result);
        var after = await ReadAsync(orderId);
        Assert.Equal(OrderStatus.Cancelled, after.InternalStatus);
        Assert.Null(after.AcceptedAt);
        Assert.Equal(syncVersion, after.ChangeVersion);
        Assert.Empty(_notifier.VersionsFor(orderId));
    }

    [Fact]
    public async Task AClaimHeldByAnotherSession_RefusesTheAction_AndEndsWithThatSession()
    {
        var orderId = await SeedOrderAsync(OrderStatus.New);
        _provider.Reset();
        _notifier.Reset();

        // Another process holds the order's transition claim, then dies (its session ends).
        var holder = new SqlConnection(_database.ConnectionString("crashed-process"));
        await holder.OpenAsync(Ct);
        await using (var command = holder.CreateCommand())
        {
            command.CommandText =
                "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', " +
                "@LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
            command.Parameters.AddWithValue("@resource", $"Wasla.OrderAction.{orderId:D}");
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(Ct)) >= 0);
        }

        var whileHeld = await Instance("operator").TryApproveAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(false, InvalidStatus), whileHeld);
        Assert.Empty(_provider.CallsFor(orderId));

        SqlConnection.ClearPool(holder);
        await holder.DisposeAsync();

        var afterCrash = await Instance("operator").TryApproveAsync(_tenantId, orderId, Ct);
        Assert.Equal(new OrderActionResult(true, "Orders.ApproveSuccess"), afterCrash);
        Assert.Equal(["accept"], _provider.CallsFor(orderId));
    }

    private OrderActionService Instance(string name) =>
        new(_database.Instance(name), [_provider], _notifier, NullLogger<OrderActionService>.Instance);

    /// <summary>Starts every caller at the same moment from its own thread and waits for all of them.</summary>
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

    private static void AssertSingleWinner(IReadOnlyCollection<OrderActionResult> results, string successKey)
    {
        Assert.Equal(new OrderActionResult(true, successKey), Assert.Single(results, r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(new OrderActionResult(false, InvalidStatus), r));
    }

    private async Task<Order> ReadAsync(Guid orderId)
    {
        await using var db = _database.Open();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
    }

    private async Task EnableAutoApproveAsync()
    {
        await EnsureTenantRowsAsync();
        await using var db = _database.Open();
        await db.TenantOperationalSettings.Where(s => s.Id == SettingsId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.AutoApproveNewOrders, true)
            .SetProperty(x => x.OperationalMode, TenantOperationalMode.Live), Ct);
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
                StoreId = "store-concurrency",
                EncryptedApiKey = "fake-key",
                EncryptedApiSecret = "fake-secret",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedOrderAsync(OrderStatus status)
    {
        await EnsureTenantRowsAsync();
        await using var db = _database.Open();
        var orderId = Guid.NewGuid();
        var externalId = $"RACE-{orderId:N}";
        db.Orders.Add(new Order
        {
            Id = orderId,
            Platform = FoodPlatform.TrendyolYemek,
            ExternalOrderId = externalId,
            ExternalOrderCode = $"TY-{externalId[..13]}",
            IdempotencyKey = $"TrendyolYemek:{externalId}",
            InternalStatus = status,
            PlatformStatus = "Created",
            CustomerName = "Concurrency Customer",
            CustomerPhone = "+905555555555",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = DateTime.UtcNow.AddMinutes(-5),
            ReceivedAt = DateTime.UtcNow.AddMinutes(-4),
            AcceptedAt = status is OrderStatus.New ? null : DateTime.UtcNow.AddMinutes(-3),
            RawPayloadJson = "{}",
            ChangeVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items =
            [
                new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductName = "Lahmacun",
                    Quantity = 1,
                    UnitPrice = 100m,
                    TotalPrice = 100m,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            ]
        });
        await db.SaveChangesAsync(Ct);
        return orderId;
    }

    /// <summary>A provider that records every lifecycle call per order and can delay, fail or hold calls.</summary>
    private sealed class RecordingProvider : IFoodPlatformClient
    {
        private readonly ConcurrentQueue<(string ExternalOrderId, string Action)> _calls = new();
        private TimeSpan _delay;
        private int _failCalls;
        private int _held;
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hold;

        public FoodPlatform Platform => FoodPlatform.TrendyolYemek;
        public TimeSpan? MaxFetchWindow => null;
        public Task FirstCallEntered => _entered.Task;

        public void Reset(TimeSpan delay = default, int failCalls = 0, bool holdFirstCall = false)
        {
            _calls.Clear();
            _delay = delay;
            _failCalls = failCalls;
            _held = 0;
            _hold = holdFirstCall;
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldCall() => _release.TrySetResult();

        public IReadOnlyList<string> CallsFor(Guid orderId) =>
            _calls.Where(c => c.ExternalOrderId == $"RACE-{orderId:N}").Select(c => c.Action).ToList();

        public Task<IReadOnlyCollection<ExternalOrderDto>> FetchOrdersAsync(PlatformConnection connection, OrderFetchWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<ExternalOrderDto>>([]);

        public Task AcceptOrderAsync(PlatformConnection connection, string externalOrderId, int preparationMinutes, CancellationToken ct) => Act(externalOrderId, "accept");
        public Task MarkInvoicedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act(externalOrderId, "invoiced");
        public Task MarkShippedAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act(externalOrderId, "shipped");
        public Task MarkDeliveredAsync(PlatformConnection connection, string externalOrderId, CancellationToken ct) => Act(externalOrderId, "delivered");
        public Task RejectOrderAsync(PlatformConnection connection, string externalOrderId, IReadOnlyList<string> itemIdList, int reasonId, CancellationToken ct) => Act(externalOrderId, "reject");

        private async Task Act(string externalOrderId, string action)
        {
            _calls.Enqueue((externalOrderId, action));
            _entered.TrySetResult();
            if (_hold && Interlocked.Increment(ref _held) >= 1)
                await _release.Task.ConfigureAwait(false);
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay).ConfigureAwait(false);
            if (Interlocked.Decrement(ref _failCalls) >= 0)
                throw new HttpRequestException("provider unavailable");
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
}
