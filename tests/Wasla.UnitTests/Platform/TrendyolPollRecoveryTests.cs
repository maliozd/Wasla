using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mapping;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Sync;
using Wasla.UnitTests.Diagnostics;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// The real Trendyol GO client, <see cref="OrderSyncService"/> and <see cref="OrderSyncCycleRunner"/> against an
/// in-process fake of the packages endpoint and a per-tenant SQLite database with the production tenant model. The fake
/// answers like the documented API: it returns only packages whose status is in <c>packageStatuses</c> and whose
/// modification time is within [<c>packageModificationStartDate</c>, <c>packageModificationEndDate</c>] (both ends
/// inclusive), paged by <c>size</c>. Credentials, customers and payloads are fake, and the request limiter runs on a
/// fake clock. This proves Wasla's request, checkpoint and scheduling logic, not the real provider's behaviour.
/// </summary>
public sealed class TrendyolPollRecoveryTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Overlap = OrderFetchWindowPlanner.CheckpointOverlap;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>A backfill turn moves the checkpoint one window minus the overlap it re-reads: 55 minutes.</summary>
    private static readonly TimeSpan TurnStep = Hour - Overlap;

    private const string ApiKey = "fake-api-key-a";
    private const string ApiSecret = "fake-api-secret-a";
    private const string ExecutorEmail = "executor-a@example.invalid";

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new() { Now = Noon };
    private readonly LimiterClock _limiterClock = new();
    private readonly FakeTrendyolApi _api = new();
    private readonly RecordingSideEffects _effects = new();
    private readonly CollectingLogger<OrderSyncService> _syncLog = new();
    private readonly CollectingLogger<TrendyolGoFoodPlatformClient> _clientLog = new();
    private readonly CollectingLogger<DefaultOrderStatusMapper> _mapperLog = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private TrendyolRequestRateLimiter _limiter;

    public TrendyolPollRecoveryTests()
    {
        _limiter = _limiterClock.Limiter();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstSync_WithoutCheckpoint_LooksBackOneHourOnly_AndStoresTheWindowEnd()
    {
        await SeedConnectionAsync(_tenant, checkpoint: null);
        _api.Put("recent", "Created", Noon.AddMinutes(-30));
        _api.Put("older", "Created", Noon.AddHours(-2));

        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        Assert.False(result.BackfillPending);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(Noon - Hour, request.StartUtc);
        Assert.Equal(Noon, request.EndUtc);
        Assert.Equal(["recent"], await OrderIdsAsync());
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Fact]
    public async Task NextSync_StartsAtTheCheckpointMinusABoundedOverlap()
    {
        var checkpoint = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("inside-overlap", "Picking", checkpoint.AddMinutes(-4));
        _api.Put("before-overlap", "Picking", checkpoint.AddMinutes(-6));
        _api.Put("after-checkpoint", "Created", checkpoint.AddMinutes(3));

        await RunAsync();

        var request = Assert.Single(_api.Requests);
        Assert.Equal(checkpoint - Overlap, request.StartUtc);
        Assert.Equal(Noon, request.EndUtc);
        Assert.Equal(["after-checkpoint", "inside-overlap"], await OrderIdsAsync());
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Fact]
    public async Task CurrentPassAfterAnOutage_FetchesTheHotWindowOnly_AndDoesNotJumpTheCheckpoint()
    {
        var checkpoint = Noon.AddHours(-6);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("new-after-outage", "Created", Noon.AddMinutes(-10));
        _api.Put("during-outage", "Delivered", Noon.AddHours(-4));

        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        Assert.True(result.BackfillPending);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(Noon - Hour, request.StartUtc);
        Assert.Equal(Noon, request.EndUtc);
        Assert.Equal(["new-after-outage"], await OrderIdsAsync());
        Assert.Equal(checkpoint, await CheckpointAsync());
        Assert.Contains(_syncLog.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Order history is behind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutageLongerThanOneHour_HotWindowFirst_ThenDeliveredCancelledAndUnSuppliedOldestFirst()
    {
        var lastGoodSync = Noon.AddHours(-6);
        _clock.Now = lastGoodSync;
        await SeedConnectionAsync(_tenant, checkpoint: null);
        _api.Put("delivered", "Shipped", lastGoodSync.AddMinutes(-20));
        _api.Put("cancelled", "Picking", lastGoodSync.AddMinutes(-15));
        _api.Put("unsupplied", "Created", lastGoodSync.AddMinutes(-10));
        await RunAsync();
        var before = await OrdersAsync();
        Assert.Equal(OrderStatus.OnTheWay, before["delivered"].InternalStatus);
        Assert.Equal(OrderStatus.Accepted, before["cancelled"].InternalStatus);
        Assert.Equal(OrderStatus.New, before["unsupplied"].InternalStatus);
        Assert.Equal(lastGoodSync, await CheckpointAsync());

        // Six hours without a successful sync. Trendyol GO moves every package meanwhile.
        _api.Put("delivered", "Delivered", lastGoodSync.AddHours(1));
        _api.Put("cancelled", "Cancelled", lastGoodSync.AddHours(2).AddMinutes(30));
        _api.Put("unsupplied", "UnSupplied", lastGoodSync.AddHours(4).AddMinutes(15));
        _api.Put("arrived-during-outage", "Created", lastGoodSync.AddHours(5).AddMinutes(40));
        _api.Requests.Clear();
        _clock.Now = Noon;

        var cycle = await RunCycleAsync(_tenant);

        Assert.Equal(0, cycle.Current.Single().FailedConnections);
        Assert.Equal(Noon - Hour, _api.Requests[0].StartUtc);
        Assert.Equal(Noon, _api.Requests[0].EndUtc);
        var backfill = _api.Requests.Skip(1).ToList();
        // Six turns reach 11:30; the seventh, [11:25, 12:00], reaches the boundary of the hot window, so history is
        // complete up to now and the checkpoint is current at the end of this cycle.
        Assert.Equal(7, backfill.Count);
        Assert.Equal(lastGoodSync - Overlap, backfill[0].StartUtc);
        AssertBackfillChain(backfill);
        Assert.Equal(Noon, backfill[^1].EndUtc);
        Assert.Equal(Noon, await CheckpointAsync());
        Assert.Contains(_syncLog.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Order history recovered", StringComparison.Ordinal));

        var after = await OrdersAsync();
        Assert.Equal(4, after.Count);
        Assert.Equal(OrderStatus.Delivered, after["delivered"].InternalStatus);
        Assert.Equal("Delivered", after["delivered"].PlatformStatus);
        Assert.NotNull(after["delivered"].DeliveredAt);
        Assert.Equal(OrderStatus.Cancelled, after["cancelled"].InternalStatus);
        Assert.Equal("Cancelled", after["cancelled"].PlatformStatus);
        Assert.NotNull(after["cancelled"].CancelledAt);
        Assert.Equal(OrderStatus.Cancelled, after["unsupplied"].InternalStatus);
        Assert.Equal("UnSupplied", after["unsupplied"].PlatformStatus);
        Assert.NotNull(after["unsupplied"].CancelledAt);
        Assert.Equal(OrderStatus.New, after["arrived-during-outage"].InternalStatus);
        Assert.Equal(before["delivered"].Id, after["delivered"].Id);
        Assert.Equal(before["delivered"].CreatedAt, after["delivered"].CreatedAt);
    }

    [Fact]
    public async Task OutageLongerThanOneCycle_RecoversTwelveWindowsPerCycle_AndFinishesOnTheNext()
    {
        var checkpoint = Noon.AddHours(-20);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("early", "Delivered", checkpoint.AddHours(2));
        _api.Put("late", "Created", Noon.AddHours(-2));

        var first = await RunCycleAsync(_tenant);

        Assert.True(first.Current.Single().BackfillPending);
        Assert.Equal(1 + OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle, _api.Requests.Count);
        AssertBackfillChain(_api.Requests.Skip(1).ToList());
        var afterFirst = checkpoint + OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle * TurnStep;
        Assert.Equal(afterFirst, await CheckpointAsync());
        Assert.Equal(["early"], await OrderIdsAsync());

        _api.Requests.Clear();
        await RunCycleAsync(_tenant);

        Assert.Equal(Noon - Hour, _api.Requests[0].StartUtc);
        Assert.Equal(afterFirst - Overlap, _api.Requests[1].StartUtc);
        AssertBackfillChain(_api.Requests.Skip(1).ToList());
        Assert.Equal(Noon, _api.Requests[^1].EndUtc);
        Assert.Equal(["early", "late"], await OrderIdsAsync());
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Fact]
    public async Task EveryPageOfAWindow_IsFetchedAndStored()
    {
        await SeedConnectionAsync(_tenant, Noon.AddMinutes(-1));
        for (var i = 0; i < 120; i++)
            _api.Put($"p{i:D3}", "Created", Noon.AddSeconds(-i));

        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal([0, 1, 2], _api.Requests.Select(r => r.Page));
        Assert.All(_api.Requests, r => Assert.Equal(TrendyolGoFoodPlatformClient.FetchPageSize, r.Size));
        Assert.Equal(120, result.InsertedCount);
        Assert.Equal(120, (await OrderIdsAsync()).Length);
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Fact]
    public async Task HotAndHistoricalWindowsOverlapping_StoreOneOrderAndItem_AndRunSideEffectsOnce()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        // In both backfill windows (their five-minute overlap), in the hot window and the second backfill window,
        // and in the hot window only.
        _api.Put("in-backfill-overlap", "Picking", checkpoint + TurnStep - TimeSpan.FromMinutes(2));
        _api.Put("in-hot-and-history", "Picking", Noon.AddMinutes(-30));
        _api.Put("new-order", "Created", Noon.AddMinutes(-3));

        await RunCycleAsync(_tenant);

        // The hot window, then history up to it: [-2h05, -1h05], [-1h10, -0h10] and the last turn [-0h15, now].
        Assert.Equal(4, _api.Requests.Count);
        Assert.Equal(2, _api.Requests.Count(r => r.Contains(checkpoint + TurnStep - TimeSpan.FromMinutes(2))));
        Assert.Equal(2, _api.Requests.Count(r => r.Contains(Noon.AddMinutes(-30))));

        _clock.Now = Noon.AddMinutes(1);
        var second = await RunAsync();
        _clock.Now = Noon.AddMinutes(2);
        var third = await RunAsync();
        Assert.Equal((0, 0), (second.InsertedCount, second.UpdatedCount));
        Assert.Equal((0, 0), (third.InsertedCount, third.UpdatedCount));

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        foreach (var id in new[] { "in-backfill-overlap", "in-hot-and-history", "new-order" })
        {
            Assert.Equal(1, await db.Orders.CountAsync(o => o.ExternalOrderId == id, Ct));
            Assert.Equal(1, await db.OrderItems.CountAsync(i => i.Order!.ExternalOrderId == id, Ct));
        }

        var orders = await OrdersAsync();
        Assert.Equal(orders.Values.Select(o => o.Id).Order(), _effects.AutoApproved.Order());
        Assert.Equal(
            new[] { orders["in-backfill-overlap"].Id, orders["in-hot-and-history"].Id }.Order(),
            _effects.Receipts.Order());
    }

    [Fact]
    public async Task OlderProviderData_DoesNotMoveAnOrderBackFromATerminalOrNewerStatus()
    {
        await SeedConnectionAsync(_tenant, Noon.AddMinutes(-10));
        _api.Put("delivered", "Delivered", Noon.AddMinutes(-9));
        _api.Put("unsupplied", "UnSupplied", Noon.AddMinutes(-8));
        _api.Put("preparing", "Picking", Noon.AddMinutes(-7));
        await RunAsync();
        await SetStatusAsync("preparing", OrderStatus.Preparing);
        var before = await OrdersAsync();

        // Lagging provider data, re-read by the next run's overlap.
        _api.Put("delivered", "Shipped", Noon.AddMinutes(1));
        _api.Put("unsupplied", "Picking", Noon.AddMinutes(1));
        _api.Put("preparing", "Picking", Noon.AddMinutes(1));
        _clock.Now = Noon.AddMinutes(2);
        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        var after = await OrdersAsync();
        Assert.Equal(OrderStatus.Delivered, after["delivered"].InternalStatus);
        Assert.Equal(before["delivered"].DeliveredAt, after["delivered"].DeliveredAt);
        Assert.Equal(OrderStatus.Cancelled, after["unsupplied"].InternalStatus);
        Assert.Equal(before["unsupplied"].CancelledAt, after["unsupplied"].CancelledAt);
        Assert.Equal(OrderStatus.Preparing, after["preparing"].InternalStatus);
        Assert.Equal([before["preparing"].Id], _effects.Receipts);
    }

    [Fact]
    public async Task HotWindowFailure_SkipsBackfillForThatConnection()
    {
        var checkpoint = Noon.AddHours(-3);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("history", "Created", checkpoint.AddMinutes(10));
        _api.Respond = r => r.StartUtc == Noon - Hour ? Json("<html>gateway</html>") : null;

        var cycle = await RunCycleAsync(_tenant);

        Assert.Equal(1, cycle.Current.Single().FailedConnections);
        Assert.Empty(cycle.Backfill);
        Assert.Single(_api.Requests);
        Assert.Equal(checkpoint, await CheckpointAsync());
        Assert.Empty(await OrderIdsAsync());
    }

    [Fact]
    public async Task FailedHistoricalPage_KeepsTheLastCompletedCheckpoint_AndTheHotOrders()
    {
        var checkpoint = Noon.AddHours(-3);
        await SeedConnectionAsync(_tenant, checkpoint);
        var firstTurnEnd = checkpoint + TurnStep;
        var secondTurnStart = firstTurnEnd - Overlap;
        _api.Put("first-window", "Delivered", checkpoint.AddMinutes(10));
        for (var i = 0; i < 60; i++)
            _api.Put($"second-window-{i:D2}", "Created", checkpoint.AddHours(1).AddSeconds(i));
        _api.Put("hot-order", "Created", Noon.AddMinutes(-30));
        _api.Respond = r => r.StartUtc == secondTurnStart && r.Page == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(FakeTrendyolApi.SensitiveBody) }
            : null;

        var failed = await RunCycleAsync(_tenant);

        Assert.Equal(1, failed.Backfill.Sum(r => r.FailedConnections));
        Assert.Equal(firstTurnEnd, await CheckpointAsync());
        AssertNoBackfillAfter(secondTurnStart);
        Assert.Equal(["first-window", "hot-order"], await OrderIdsAsync());
        await AssertSingleFailedSyncAsync("ProviderRequestException");

        _api.Respond = null;
        _api.Requests.Clear();
        await RunCycleAsync(_tenant);

        Assert.Equal(secondTurnStart, _api.Requests[1].StartUtc);
        Assert.Equal(62, (await OrderIdsAsync()).Length);
        await RunAsync();
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Theory]
    [InlineData("<html>gateway</html>", "JsonException")]
    [InlineData("""{"page":0,"size":50,"totalPages":-1,"totalCount":0,"content":[]}""", "InvalidOperationException")]
    public async Task MalformedHistoricalResponse_KeepsTheLastCompletedCheckpoint(string body, string errorType)
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        var secondTurnStart = checkpoint + TurnStep - Overlap;
        _api.Put("first-window", "Created", checkpoint);
        _api.Put("second-window", "Created", checkpoint.AddMinutes(57));
        _api.Respond = r => r.StartUtc == secondTurnStart ? Json(body) : null;

        await RunCycleAsync(_tenant);

        Assert.Equal(checkpoint + TurnStep, await CheckpointAsync());
        Assert.Equal(["first-window"], await OrderIdsAsync());
        AssertNoBackfillAfter(secondTurnStart);
        await AssertSingleFailedSyncAsync(errorType);
    }

    [Fact]
    public async Task PersistenceFailureInHistory_KeepsTheLastCompletedCheckpoint()
    {
        var checkpoint = Noon.AddHours(-3);
        _clock.Now = checkpoint;
        await SeedConnectionAsync(_tenant, checkpoint: null);
        _api.Put("existing", "Picking", checkpoint.AddMinutes(-30));
        await RunAsync();
        Assert.Equal(checkpoint, await CheckpointAsync());

        _api.Put("existing", "Shipped", checkpoint.AddMinutes(20));
        _api.Put("new-in-second-window", "Created", checkpoint.AddMinutes(80));
        _api.Requests.Clear();
        _clock.Now = Noon;
        _tenants.FailNextStatementContaining = "INSERT INTO \"Orders\"";

        await RunCycleAsync(_tenant);

        Assert.Equal(checkpoint + TurnStep, await CheckpointAsync());
        Assert.Equal(OrderStatus.OnTheWay, (await OrdersAsync())["existing"].InternalStatus);
        AssertNoBackfillAfter(checkpoint + TurnStep - Overlap);
        await AssertSingleFailedSyncAsync("DbUpdateException");

        await RunCycleAsync(_tenant);
        await RunAsync();

        Assert.Equal(["existing", "new-in-second-window"], await OrderIdsAsync());
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Fact]
    public async Task PageCapReachedWhileMorePagesRemain_FailsAndKeepsTheCheckpoint()
    {
        var checkpoint = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpoint);
        var reportedPages = TrendyolGoFoodPlatformClient.MaxFetchPages + 5;
        _api.Respond = r => Json(FakeTrendyolApi.PageJson(
            r.Page,
            r.Size,
            reportedPages,
            reportedPages,
            [FakeTrendyolApi.PackageJson($"cap-{r.Page}", "Created", Noon.AddMinutes(-1), Noon.AddMinutes(-2))]));

        var result = await RunAsync();

        Assert.Equal(1, result.FailedConnections);
        Assert.Equal(TrendyolGoFoodPlatformClient.MaxFetchPages, _api.Requests.Count);
        Assert.Single(_api.Requests.Select(r => (r.StartMs, r.EndMs)).Distinct());
        Assert.Equal(checkpoint, await CheckpointAsync());
        Assert.Empty(await OrderIdsAsync());
        Assert.Contains(_clientLog.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("page cap reached", StringComparison.Ordinal));
        await AssertSingleFailedSyncAsync("InvalidOperationException");
    }

    [Fact]
    public async Task ThrottledUntilRetriesRunOut_FailsTheWindow_WithoutAWholeWindowRetry_AndKeepsTheCheckpoint()
    {
        var checkpoint = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("waiting", "Created", Noon.AddMinutes(-1));
        _api.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(FakeTrendyolApi.SensitiveBody) };

        var result = await RunAsync();

        Assert.Equal(1, result.FailedConnections);
        Assert.Equal(1 + TrendyolGoFoodPlatformClient.MaxThrottledRetries, _api.Requests.Count);
        Assert.All(_api.Requests, r => Assert.Equal(0, r.Page));
        Assert.Equal(checkpoint, await CheckpointAsync());
        Assert.Empty(await OrderIdsAsync());
        await AssertSingleFailedSyncAsync("ProviderRequestException");
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.DoesNotContain(await db.SyncLogs.ToListAsync(Ct), s => s.Status == SyncStatus.Success);
    }

    [Fact]
    public async Task HostCancellationDuringBackfill_KeepsCompletedWindows_AndRecordsNoFailure()
    {
        var checkpoint = Noon.AddHours(-3);
        await SeedConnectionAsync(_tenant, checkpoint);
        var secondTurnStart = checkpoint + TurnStep - Overlap;
        _api.Put("first-window", "Created", checkpoint);
        using var cts = new CancellationTokenSource();
        _api.Respond = r =>
        {
            if (r.StartUtc == secondTurnStart)
                cts.Cancel();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCycleAsync(cts.Token, _tenant));

        Assert.Equal(checkpoint + TurnStep, await CheckpointAsync());
        Assert.Equal(["first-window"], await OrderIdsAsync());
        AssertNoBackfillAfter(secondTurnStart);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.DoesNotContain(await db.SyncLogs.ToListAsync(Ct), s => s.Status == SyncStatus.Failed);
        Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
        Assert.Equal(0, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
    }

    [Fact]
    public async Task HostCancellationWhileWaitingForThePermit_StopsPromptly_AndRecordsNoFailure()
    {
        var checkpoint = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpoint);
        using var cts = new CancellationTokenSource();
        _limiter = new TrendyolRequestRateLimiter(
            _limiterClock,
            async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            permitLimit: 1,
            TrendyolRequestRateLimiter.DefaultWindow);
        await _limiter.AcquireAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sync().SyncCustomerWithResultAsync(_tenant, cts.Token).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.Empty(_api.Requests);
        Assert.Equal(checkpoint, await CheckpointAsync());
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Empty(await db.SyncLogs.ToListAsync(Ct));
        Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
    }

    [Fact]
    public async Task Tenants_KeepTheirOwnCheckpointCredentialsAndOrders()
    {
        var other = Guid.NewGuid();
        var checkpointA = Noon.AddHours(-3);
        var checkpointB = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpointA, "supplier-a", ApiKey, ApiSecret, ExecutorEmail);
        await SeedConnectionAsync(other, checkpointB, "supplier-b", "fake-api-key-b", "fake-api-secret-b", "executor-b@example.invalid");
        var secondTurnStartA = checkpointA + TurnStep - Overlap;
        _api.Put("a-order", "Delivered", checkpointA.AddMinutes(30), "supplier-a");
        _api.Put("b-order", "Created", Noon.AddMinutes(-2), "supplier-b");
        _api.Respond = r => r.SupplierId == "supplier-a" && r.StartUtc == secondTurnStartA ? Json("not json") : null;

        var cycle = await RunCycleAsync(_tenant, other);

        Assert.Equal(1, cycle.Backfill.Where(r => r.CustomerId == _tenant).Sum(r => r.FailedConnections));
        Assert.Equal(0, cycle.Current.Single(r => r.CustomerId == other).FailedConnections);
        var requestB = Assert.Single(_api.Requests, r => r.SupplierId == "supplier-b");
        Assert.Equal(checkpointB - Overlap, requestB.StartUtc);
        Assert.Equal(BasicAuth("fake-api-key-b", "fake-api-secret-b"), requestB.Authorization);
        Assert.Equal("executor-b@example.invalid", requestB.ExecutorUser);
        var requestsA = _api.Requests.Where(r => r.SupplierId == "supplier-a").ToList();
        Assert.Equal(3, requestsA.Count);
        Assert.All(requestsA, r => Assert.Equal(BasicAuth(ApiKey, ApiSecret), r.Authorization));
        Assert.All(requestsA, r => Assert.Equal(ExecutorEmail, r.ExecutorUser));
        Assert.Equal(["a-order"], await OrderIdsAsync(_tenant));
        Assert.Equal(["b-order"], await OrderIdsAsync(other));
        Assert.Equal(checkpointA + TurnStep, await CheckpointAsync(_tenant));
        Assert.Equal(Noon, await CheckpointAsync(other));
    }

    [Fact]
    public async Task TenantWithALargeBackfill_DoesNotDelayAnotherTenantsCurrentWindow()
    {
        var other = Guid.NewGuid();
        await SeedConnectionAsync(_tenant, Noon.AddHours(-48), "supplier-a");
        await SeedConnectionAsync(other, Noon.AddMinutes(-10), "supplier-b", "fake-api-key-b", "fake-api-secret-b");

        await RunCycleAsync(_tenant, other);

        var suppliers = _api.Requests.Select(r => r.SupplierId).ToList();
        var currentB = suppliers.IndexOf("supplier-b");
        var firstBackfillA = suppliers.FindIndex(1 + suppliers.IndexOf("supplier-a"), s => s == "supplier-a");
        Assert.True(currentB >= 0);
        Assert.True(firstBackfillA > currentB);
        Assert.Equal(1 + 1 + OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle, _api.Requests.Count);
        Assert.Equal(Noon, await CheckpointAsync(other));
    }

    // ---- Checkpoint convergence: after recovery the checkpoint reaches the current boundary in the same cycle. ----

    [Theory]
    [InlineData(30)]
    [InlineData(55)]
    public async Task CheckpointInsideTheCurrentWindow_IsCoveredByIt_AndBecomesCurrent_WithoutBackfill(int minutesBehind)
    {
        var checkpoint = Noon.AddMinutes(-minutesBehind);
        await SeedConnectionAsync(_tenant, checkpoint);

        var cycle = await RunCycleAsync(_tenant);

        var request = Assert.Single(_api.Requests);
        Assert.Equal(checkpoint - Overlap, request.StartUtc);
        Assert.Equal(Noon, request.EndUtc);
        Assert.False(cycle.Current.Single().BackfillPending);
        Assert.Empty(cycle.Backfill);
        Assert.Equal(Noon, await CheckpointAsync());
        Assert.DoesNotContain(_syncLog.Entries, e => e.Message.Contains("Order history is behind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoHoursBehind_BackfillReachesTheHotBoundary_AndTheCheckpointBecomesCurrentInTheSameCycle()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("in-history", "Delivered", checkpoint.AddMinutes(20));
        _api.Put("in-hot-window", "Created", Noon.AddMinutes(-20));

        var cycle = await RunCycleAsync(_tenant);

        Assert.Equal(Noon - Hour, _api.Requests[0].StartUtc);
        Assert.Equal(Noon, _api.Requests[0].EndUtc);
        var backfill = _api.Requests.Skip(1).ToList();
        Assert.Equal(checkpoint - Overlap, backfill[0].StartUtc);
        AssertBackfillChain(backfill);
        Assert.Equal(Noon, backfill[^1].EndUtc);
        Assert.False(cycle.Backfill[^1].BackfillPending);
        Assert.Equal(Noon, await CheckpointAsync());
        Assert.Equal(["in-history", "in-hot-window"], await OrderIdsAsync());
        Assert.Contains(_syncLog.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Order history recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackfillBudgetEndingBeforeTheHotBoundary_KeepsTheLastCompletedHistoricalWindow()
    {
        var checkpoint = Noon.AddHours(-5);
        await SeedConnectionAsync(_tenant, checkpoint);
        // Each historical request takes 20 seconds of the 30-second phase budget.
        _api.Respond = r =>
        {
            if (r.StartUtc != Noon - Hour)
                _clock.Now = _clock.Now.AddSeconds(20);
            return null;
        };

        var cycle = await RunCycleAsync(_tenant);

        Assert.Equal(2, cycle.Backfill.Count);
        Assert.True(cycle.Backfill[^1].BackfillPending);
        Assert.Equal(checkpoint + 2 * TurnStep, await CheckpointAsync());
    }

    [Fact]
    public async Task BackfillFailingBeforeTheHotBoundary_KeepsTheLastCompletedWindow_AndTheHotOrders()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("in-hot-window", "Created", Noon.AddMinutes(-20));
        _api.Respond = r => r.StartUtc == checkpoint + TurnStep - Overlap ? Json("<html>gateway</html>") : null;

        var cycle = await RunCycleAsync(_tenant);

        Assert.Equal(1, cycle.Backfill[^1].FailedConnections);
        Assert.Equal(checkpoint + TurnStep, await CheckpointAsync());
        Assert.Equal(["in-hot-window"], await OrderIdsAsync());
    }

    [Fact]
    public async Task FollowingCycle_DoesNotRecoverACompletedGapAgain()
    {
        await SeedConnectionAsync(_tenant, Noon.AddHours(-2));
        await RunCycleAsync(_tenant);
        Assert.Equal(Noon, await CheckpointAsync());
        _api.Requests.Clear();
        var behindLogs = _syncLog.Entries.Count(e => e.Message.Contains("Order history is behind", StringComparison.Ordinal));

        _clock.Now = Noon.AddSeconds(30);
        var next = await RunCycleAsync(_tenant);

        var request = Assert.Single(_api.Requests);
        Assert.Equal(Noon - Overlap, request.StartUtc);
        Assert.Equal(Noon.AddSeconds(30), request.EndUtc);
        Assert.Empty(next.Backfill);
        Assert.Equal(behindLogs, _syncLog.Entries.Count(e => e.Message.Contains("Order history is behind", StringComparison.Ordinal)));
        Assert.Equal(Noon.AddSeconds(30), await CheckpointAsync());
    }

    [Fact]
    public async Task DisplayedLastSuccessfulSync_IsTheCurrentBoundaryAfterFullRecovery()
    {
        await SeedConnectionAsync(_tenant, Noon.AddHours(-6));
        var connections = new Wasla.Infrastructure.Services.PlatformConnectionService(
            _tenants,
            new PassthroughSecrets(),
            new Wasla.Application.PlatformConnections.CreatePlatformConnectionCommandValidator());

        await RunCycleAsync(_tenant);

        var shown = Assert.Single(await connections.GetListAsync(_tenant, Ct)).LastSuccessfulSyncUtc;
        Assert.NotNull(shown);
        Assert.Equal(Noon, DateTime.SpecifyKind(shown.Value, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Logs_DoNotContainCredentialsCustomerDetailsOrProviderPayloads()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        var secondTurnStart = checkpoint + TurnStep - Overlap;
        _api.Put("logged-1", "Picking", checkpoint.AddMinutes(1));
        _api.Put("logged-2", "UnSupplied", checkpoint.AddMinutes(2));
        _api.Put("logged-hot", "Created", Noon.AddMinutes(-5));
        var throttledOnce = false;
        _api.Respond = r =>
        {
            if (r.StartUtc == Noon - Hour && !throttledOnce)
            {
                throttledOnce = true;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(FakeTrendyolApi.SensitiveBody) };
            }

            return r.StartUtc == secondTurnStart ? Json(FakeTrendyolApi.TruncatedSensitiveBody) : null;
        };

        await RunCycleAsync(_tenant);

        var entries = _syncLog.Entries.Concat(_clientLog.Entries).Concat(_mapperLog.Entries).ToList();
        Assert.Contains(entries, e => e.Level == LogLevel.Error && e.Message.Contains("WindowStartUtc=", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Message.Contains("throttled", StringComparison.Ordinal));
        string[] forbidden =
        [
            ApiKey,
            ApiSecret,
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiKey}:{ApiSecret}")),
            "Basic ",
            ExecutorEmail,
            "Ayşe",
            "Gizlisoy",
            "5321112233",
            "Saklı Sokak",
            "Zile basmayın",
            "\"lines\"",
            "\"customer\""
        ];
        foreach (var entry in entries)
        {
            var text = entry.Message + "\n" + entry.Exception;
            foreach (var value in forbidden)
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var stored = (await db.SyncLogs.Select(s => s.ErrorMessage).ToListAsync(Ct))
            .Concat(await db.IntegrationErrors.Select(e => e.ErrorMessage).ToListAsync(Ct));
        foreach (var message in stored)
        {
            foreach (var value in forbidden)
                Assert.DoesNotContain(value, message ?? string.Empty, StringComparison.Ordinal);
        }
    }

    public void Dispose() => _tenants.Dispose();

    private OrderSyncService Sync()
    {
        var mapper = new DefaultOrderStatusMapper(_mapperLog);
        var client = new TrendyolGoFoodPlatformClient(
            new HttpClient(new DelegateHandler(_api.Handle)) { BaseAddress = new Uri("https://trendyol.example/") },
            new PassthroughSecrets(),
            mapper,
            Options.Create(new TrendyolGoOptions { AgentName = "Wasla", BaseUrl = "https://trendyol.example/" }),
            _clientLog,
            _limiter,
            _clock);

        return new OrderSyncService(
            _tenants,
            [client],
            mapper,
            _effects,
            _effects,
            _syncLog,
            businessSubtypeReader: null,
            time: _clock);
    }

    /// <summary>The current pass only, as one tenant's phase 1.</summary>
    private Task<OrderSyncCustomerResult> RunAsync(Guid? tenant = null) =>
        Sync().SyncCustomerWithResultAsync(tenant ?? _tenant, Ct);

    /// <summary>One Worker cycle: every tenant's current pass, then round-robin backfill turns.</summary>
    private Task<OrderSyncCycleResult> RunCycleAsync(params Guid[] tenants) => RunCycleAsync(Ct, tenants);

    private Task<OrderSyncCycleResult> RunCycleAsync(CancellationToken ct, params Guid[] tenants) =>
        new OrderSyncCycleRunner(_clock, maxParallelTenants: 1, OrderSyncCycleRunner.BackfillBudget).RunAsync(
            tenants,
            async (id, token) => await Sync().SyncCustomerWithResultAsync(id, token),
            async (id, token) => await Sync().BackfillCustomerAsync(id, token),
            ct);

    private async Task SeedConnectionAsync(
        Guid tenant,
        DateTime? checkpoint,
        string supplier = "supplier-a",
        string apiKey = ApiKey,
        string apiSecret = ApiSecret,
        string executor = ExecutorEmail)
    {
        await _tenants.SeedSettingsAsync(tenant, TenantOperationalMode.Live);
        await using var db = await _tenants.CreateAsync(tenant, Ct);
        db.PlatformConnections.Add(new PlatformConnection
        {
            Platform = FoodPlatform.TrendyolYemek,
            SupplierId = supplier,
            StoreId = "store-" + supplier,
            ExecutorEmail = executor,
            EncryptedApiKey = apiKey,
            EncryptedApiSecret = apiSecret,
            IsActive = true,
            SyncIntervalSeconds = 0,
            LastSuccessfulSync = checkpoint
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<DateTime?> CheckpointAsync(Guid? tenant = null)
    {
        await using var db = await _tenants.CreateAsync(tenant ?? _tenant, Ct);
        var stored = await db.PlatformConnections.Select(c => c.LastSuccessfulSync).SingleAsync(Ct);
        return stored is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    private async Task<Dictionary<string, Order>> OrdersAsync(Guid? tenant = null)
    {
        await using var db = await _tenants.CreateAsync(tenant ?? _tenant, Ct);
        return (await db.Orders.AsNoTracking().ToListAsync(Ct)).ToDictionary(o => o.ExternalOrderId, StringComparer.Ordinal);
    }

    private async Task<string[]> OrderIdsAsync(Guid? tenant = null)
    {
        await using var db = await _tenants.CreateAsync(tenant ?? _tenant, Ct);
        return (await db.Orders.Select(o => o.ExternalOrderId).ToListAsync(Ct)).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task SetStatusAsync(string externalOrderId, OrderStatus status)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var order = await db.Orders.SingleAsync(o => o.ExternalOrderId == externalOrderId, Ct);
        order.InternalStatus = status;
        await db.SaveChangesAsync(Ct);
    }

    private async Task AssertSingleFailedSyncAsync(params string[] acceptedErrorTypes)
    {
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var failed = await db.SyncLogs.Where(s => s.Status == SyncStatus.Failed).ToListAsync(Ct);
        Assert.Single(failed);
        var error = Assert.Single(await db.IntegrationErrors.ToListAsync(Ct));
        Assert.Contains(error.ErrorType, acceptedErrorTypes);
        Assert.Equal(1, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
    }

    /// <summary>
    /// No backfill window after the one that stopped the run was requested. The hot window (fetched first, ending now)
    /// is not a backfill window.
    /// </summary>
    private void AssertNoBackfillAfter(DateTime lastAttemptedStart) =>
        Assert.DoesNotContain(_api.Requests, r => r.StartUtc > lastAttemptedStart && r.StartUtc != _clock.UtcNow - Hour);

    /// <summary>
    /// Backfill windows (first page of each) start at the previous window's end minus the overlap they re-read, are at
    /// most one hour long, and move forward.
    /// </summary>
    private static void AssertBackfillChain(IReadOnlyList<ApiRequest> requests)
    {
        var windows = requests.Where(r => r.Page == 0).ToList();
        Assert.NotEmpty(windows);
        for (var i = 0; i < windows.Count; i++)
        {
            Assert.True(windows[i].EndMs > windows[i].StartMs);
            Assert.True(windows[i].EndUtc - windows[i].StartUtc <= TrendyolGoFoodPlatformClient.FetchWindowLength);
            if (i > 0)
                Assert.Equal(windows[i - 1].EndUtc - Overlap, windows[i].StartUtc);
        }
    }

    private static string BasicAuth(string key, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}"));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static long Milliseconds(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private sealed record ApiRequest(
        string SupplierId,
        IReadOnlyList<string> Statuses,
        long StartMs,
        long EndMs,
        int Page,
        int Size,
        string? Authorization,
        string? ExecutorUser)
    {
        public DateTime StartUtc => DateTimeOffset.FromUnixTimeMilliseconds(StartMs).UtcDateTime;

        public DateTime EndUtc => DateTimeOffset.FromUnixTimeMilliseconds(EndMs).UtcDateTime;

        public bool Contains(DateTime utc) => StartMs <= Milliseconds(utc) && Milliseconds(utc) <= EndMs;

        public static ApiRequest From(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            var supplier = Regex.Match(uri.AbsolutePath, "^/integrator/order/meal/suppliers/([^/]+)/packages$").Groups[1].Value;
            var query = Regex.Matches(uri.Query, @"[?&]([^=&]+)=([^&]*)")
                .ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value), StringComparer.Ordinal);
            return new ApiRequest(
                supplier,
                query.TryGetValue("packageStatuses", out var statuses) ? statuses.Split(',') : [],
                query.TryGetValue("packageModificationStartDate", out var start) ? long.Parse(start) : 0,
                query.TryGetValue("packageModificationEndDate", out var end) ? long.Parse(end) : long.MaxValue,
                int.Parse(query["page"]),
                int.Parse(query["size"]),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-executor-user", out var executor) ? executor.Single() : null);
        }
    }

    /// <summary>In-process stand-in for GET /integrator/order/meal/suppliers/{supplierId}/packages.</summary>
    private sealed class FakeTrendyolApi
    {
        public const string SensitiveBody =
            """{"customer":{"firstName":"Ayşe","lastName":"Gizlisoy"},"address":{"phone":"5321112233","address1":"Saklı Sokak 7"}}""";

        public const string TruncatedSensitiveBody =
            """{"page":0,"content":[{"id":"x","customer":{"firstName":"Ayşe","lastName":"Gizlisoy"},"address":{"phone":"5321112233","address1":"Saklı Sokak 7"},"lines":[""";

        private readonly List<Package> _packages = [];

        public List<ApiRequest> Requests { get; } = [];

        /// <summary>Returns a response instead of the package listing when it gives one.</summary>
        public Func<ApiRequest, HttpResponseMessage?>? Respond { get; set; }

        /// <summary>Creates the package or moves it to a new status and modification time.</summary>
        public void Put(string id, string status, DateTime modifiedUtc, string supplier = "supplier-a")
        {
            var existing = _packages.SingleOrDefault(p => p.Id == id && p.Supplier == supplier);
            if (existing is not null)
                _packages.Remove(existing);
            _packages.Add(new Package(id, status, modifiedUtc, existing?.CreatedUtc ?? modifiedUtc.AddMinutes(-1), supplier));
        }

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var parsed = ApiRequest.From(request);
            Requests.Add(parsed);
            if (Respond?.Invoke(parsed) is { } custom)
                return custom;

            var matching = _packages
                .Where(p => p.Supplier == parsed.SupplierId
                            && parsed.Statuses.Contains(p.Status, StringComparer.Ordinal)
                            && parsed.Contains(p.ModifiedUtc))
                .OrderBy(p => p.ModifiedUtc)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();
            var totalPages = Math.Max(1, (matching.Count + parsed.Size - 1) / parsed.Size);
            var content = matching
                .Skip(parsed.Page * parsed.Size)
                .Take(parsed.Size)
                .Select(p => PackageJson(p.Id, p.Status, p.ModifiedUtc, p.CreatedUtc));
            return Json(PageJson(parsed.Page, parsed.Size, totalPages, matching.Count, content));
        }

        public static string PageJson(int page, int size, int totalPages, int totalCount, IEnumerable<string> packages) =>
            $$"""{"page":{{page}},"size":{{size}},"totalPages":{{totalPages}},"totalCount":{{totalCount}},"content":[{{string.Join(',', packages)}}]}""";

        public static string PackageJson(string id, string status, DateTime modifiedUtc, DateTime createdUtc) =>
            $$"""
            {"id":"{{id}}","orderNumber":"N-{{id}}","packageCreationDate":{{Milliseconds(createdUtc)}},"packageModificationDate":{{Milliseconds(modifiedUtc)}},"lastModifiedDate":{{Milliseconds(modifiedUtc)}},"packageStatus":"{{status}}","totalPrice":42.5,"customer":{"firstName":"Ayşe","lastName":"Gizlisoy"},"address":{"phone":"5321112233","address1":"Saklı Sokak 7","city":"İstanbul","neighborhood":"Moda"},"lines":[{"name":"Lahmacun","unitSellingPrice":42.5,"items":[{"isCancelled":false,"packageItemId":"{{id}}-1"}]}],"customerNote":"Zile basmayın"}
            """;

        private sealed record Package(string Id, string Status, DateTime ModifiedUtc, DateTime CreatedUtc, string Supplier);
    }

    private sealed class RecordingSideEffects : IOrderAutoApproveService, IOrderReceiptCreationService
    {
        public List<Guid> AutoApproved { get; } = [];

        public List<Guid> Receipts { get; } = [];

        public Task ProcessNewlyInsertedOrderAsync(Guid customerId, Guid orderId, OrderStatus insertedStatus, CancellationToken ct)
        {
            AutoApproved.Add(orderId);
            return Task.CompletedTask;
        }

        public Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
        {
            Receipts.Add(orderId);
            return Task.CompletedTask;
        }
    }
}
