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
/// The real Trendyol GO client and <see cref="OrderSyncService"/> against an in-process fake of the packages endpoint
/// and a per-tenant SQLite database with the production tenant model. The fake answers like the documented API: it
/// returns only packages whose status is in <c>packageStatuses</c> and whose modification time is within
/// [<c>packageModificationStartDate</c>, <c>packageModificationEndDate</c>] (both ends inclusive), paged by
/// <c>size</c>. Credentials, customers and payloads are fake. This proves Wasla's request and checkpoint logic, not the
/// real provider's behaviour.
/// </summary>
public sealed class TrendyolPollRecoveryTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Overlap = OrderFetchWindowPlanner.CheckpointOverlap;

    private const string ApiKey = "fake-api-key-a";
    private const string ApiSecret = "fake-api-secret-a";
    private const string ExecutorEmail = "executor-a@example.invalid";

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly OperationalModeTestClock _clock = new() { Now = Noon };
    private readonly FakeTrendyolApi _api = new();
    private readonly RecordingSideEffects _effects = new();
    private readonly CollectingLogger<OrderSyncService> _syncLog = new();
    private readonly CollectingLogger<TrendyolGoFoodPlatformClient> _clientLog = new();
    private readonly CollectingLogger<DefaultOrderStatusMapper> _mapperLog = new();
    private readonly Guid _tenant = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstSync_WithoutCheckpoint_LooksBackOneHourOnly_AndStoresTheWindowEnd()
    {
        await SeedConnectionAsync(_tenant, checkpoint: null);
        _api.Put("recent", "Created", Noon.AddMinutes(-30));
        _api.Put("older", "Created", Noon.AddHours(-2));

        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(Noon.AddHours(-1), request.StartUtc);
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
    public async Task OutageLongerThanOneHour_RecoversDeliveredCancelledAndUnSupplied_InConsecutiveWindows()
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

        var result = await RunAsync();

        Assert.Equal(0, result.FailedConnections);
        Assert.Equal(7, _api.Requests.Count);
        Assert.Equal(lastGoodSync - Overlap, _api.Requests[0].StartUtc);
        Assert.Equal(Noon, _api.Requests[^1].EndUtc);
        AssertConsecutiveWindows(_api.Requests);

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
        Assert.Equal(Noon, await CheckpointAsync());
        Assert.Contains(_syncLog.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Recovering platform orders after a sync gap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutageLongerThanOneRun_AdvancesWindowByWindow_AndFinishesOnTheNextRun()
    {
        var checkpoint = Noon.AddHours(-(OrderFetchWindowPlanner.MaxWindowsPerRun + 8));
        await SeedConnectionAsync(_tenant, checkpoint);
        _api.Put("early", "Delivered", checkpoint.AddHours(2));
        _api.Put("late", "Created", Noon.AddHours(-2));

        var first = await RunAsync();

        Assert.Equal(0, first.FailedConnections);
        Assert.Equal(OrderFetchWindowPlanner.MaxWindowsPerRun, _api.Requests.Count);
        AssertConsecutiveWindows(_api.Requests);
        var firstRunEnd = checkpoint - Overlap + TimeSpan.FromHours(OrderFetchWindowPlanner.MaxWindowsPerRun);
        Assert.Equal(firstRunEnd, _api.Requests[^1].EndUtc);
        Assert.Equal(firstRunEnd, await CheckpointAsync());
        Assert.Equal(["early"], await OrderIdsAsync());
        Assert.Contains(_syncLog.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("MoreRunsNeeded=True", StringComparison.Ordinal));

        _api.Requests.Clear();
        var second = await RunAsync();

        Assert.Equal(0, second.FailedConnections);
        Assert.Equal(firstRunEnd - Overlap, _api.Requests[0].StartUtc);
        Assert.Equal(Noon, _api.Requests[^1].EndUtc);
        AssertConsecutiveWindows(_api.Requests);
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
    public async Task RepeatedAndOverlappingResults_StoreOneOrder_AndRunSideEffectsOnce()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        var sharedBoundary = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("on-boundary", "Picking", sharedBoundary);
        _api.Put("new-order", "Created", Noon.AddMinutes(-3));

        var first = await RunAsync();

        Assert.Equal(2, _api.Requests.Count(r => r.Contains(sharedBoundary)));
        Assert.Equal(2, first.InsertedCount);
        Assert.Equal(1, first.UnchangedCount);

        _clock.Now = Noon.AddMinutes(1);
        var second = await RunAsync();
        _clock.Now = Noon.AddMinutes(2);
        var third = await RunAsync();

        Assert.Equal((0, 0, 1), (second.InsertedCount, second.UpdatedCount, second.UnchangedCount));
        Assert.Equal((0, 0, 1), (third.InsertedCount, third.UpdatedCount, third.UnchangedCount));

        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Equal(1, await db.Orders.CountAsync(o => o.ExternalOrderId == "on-boundary", Ct));
        Assert.Equal(1, await db.Orders.CountAsync(o => o.ExternalOrderId == "new-order", Ct));
        var orders = await OrdersAsync();
        Assert.Equal(
            new[] { orders["new-order"].Id, orders["on-boundary"].Id }.Order(),
            _effects.AutoApproved.Order());
        Assert.Equal([orders["on-boundary"].Id], _effects.Receipts);
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
    public async Task FailedPage_KeepsTheCheckpointAtTheLastCompleteWindow_AndTheNextRunResumesThere()
    {
        var checkpoint = Noon.AddHours(-3);
        await SeedConnectionAsync(_tenant, checkpoint);
        var firstWindowEnd = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("first-window", "Delivered", checkpoint.AddMinutes(10));
        for (var i = 0; i < 60; i++)
            _api.Put($"second-window-{i:D2}", "Created", firstWindowEnd.AddMinutes(10).AddSeconds(i));
        _api.Put("third-window", "Created", Noon.AddMinutes(-30));
        _api.Respond = r => r.StartUtc == firstWindowEnd && r.Page == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(FakeTrendyolApi.SensitiveBody) }
            : null;

        var failed = await RunAsync();

        Assert.Equal(1, failed.FailedConnections);
        Assert.Equal(firstWindowEnd, await CheckpointAsync());
        Assert.DoesNotContain(_api.Requests, r => r.StartUtc > firstWindowEnd);
        Assert.Equal(["first-window"], await OrderIdsAsync());
        await AssertSingleFailedSyncAsync("HttpRequestException", "ProviderRequestException");

        _api.Respond = null;
        _api.Requests.Clear();
        var recovered = await RunAsync();

        Assert.Equal(0, recovered.FailedConnections);
        Assert.Equal(firstWindowEnd - Overlap, _api.Requests[0].StartUtc);
        Assert.Equal(62, (await OrderIdsAsync()).Length);
        Assert.Equal(Noon, await CheckpointAsync());
    }

    [Theory]
    [InlineData("<html>gateway</html>", "JsonException")]
    [InlineData("""{"page":0,"size":50,"totalPages":-1,"totalCount":0,"content":[]}""", "InvalidOperationException")]
    public async Task MalformedResponse_KeepsTheCheckpointAtTheLastCompleteWindow(string body, string errorType)
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        var firstWindowEnd = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("first-window", "Created", checkpoint);
        _api.Put("second-window", "Created", firstWindowEnd.AddMinutes(1));
        _api.Respond = r => r.StartUtc == firstWindowEnd ? Json(body) : null;

        var result = await RunAsync();

        Assert.Equal(1, result.FailedConnections);
        Assert.Equal(firstWindowEnd, await CheckpointAsync());
        Assert.Equal(["first-window"], await OrderIdsAsync());
        Assert.DoesNotContain(_api.Requests, r => r.StartUtc > firstWindowEnd);
        await AssertSingleFailedSyncAsync(errorType);
    }

    [Fact]
    public async Task PersistenceFailure_KeepsTheCheckpointAtTheLastCompleteWindow()
    {
        var checkpoint = Noon.AddHours(-2);
        _clock.Now = checkpoint;
        await SeedConnectionAsync(_tenant, checkpoint: null);
        _api.Put("existing", "Picking", checkpoint.AddMinutes(-30));
        await RunAsync();
        Assert.Equal(checkpoint, await CheckpointAsync());

        var firstWindowEnd = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("existing", "Shipped", checkpoint.AddMinutes(20));
        _api.Put("new-in-second-window", "Created", firstWindowEnd.AddMinutes(20));
        _api.Requests.Clear();
        _clock.Now = Noon;
        _tenants.FailNextStatementContaining = "INSERT INTO \"Orders\"";

        var failed = await RunAsync();

        Assert.Equal(1, failed.FailedConnections);
        Assert.Equal(firstWindowEnd, await CheckpointAsync());
        Assert.Equal(OrderStatus.OnTheWay, (await OrdersAsync())["existing"].InternalStatus);
        Assert.DoesNotContain(_api.Requests, r => r.StartUtc > firstWindowEnd);
        await AssertSingleFailedSyncAsync("DbUpdateException");

        var recovered = await RunAsync();

        Assert.Equal(0, recovered.FailedConnections);
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
    public async Task HostCancellationDuringRecovery_KeepsTheCheckpoint_AndRecordsNoFailure()
    {
        var checkpoint = Noon.AddHours(-3);
        await SeedConnectionAsync(_tenant, checkpoint);
        var firstWindowEnd = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("first-window", "Created", checkpoint);
        using var cts = new CancellationTokenSource();
        _api.Respond = r =>
        {
            if (r.StartUtc == firstWindowEnd)
                cts.Cancel();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sync().SyncCustomerWithResultAsync(_tenant, cts.Token));

        Assert.Equal(checkpoint, await CheckpointAsync());
        Assert.DoesNotContain(_api.Requests, r => r.StartUtc > firstWindowEnd);
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        Assert.Empty(await db.SyncLogs.ToListAsync(Ct));
        Assert.Empty(await db.IntegrationErrors.ToListAsync(Ct));
        Assert.Equal(0, (await db.PlatformConnections.SingleAsync(Ct)).ConsecutiveFailures);
    }

    [Fact]
    public async Task Tenants_KeepTheirOwnCheckpointCredentialsAndOrders()
    {
        var other = Guid.NewGuid();
        var checkpointA = Noon.AddHours(-3);
        var checkpointB = Noon.AddMinutes(-10);
        await SeedConnectionAsync(_tenant, checkpointA, "supplier-a", ApiKey, ApiSecret, ExecutorEmail);
        await SeedConnectionAsync(other, checkpointB, "supplier-b", "fake-api-key-b", "fake-api-secret-b", "executor-b@example.invalid");
        var secondWindowEndA = checkpointA - Overlap + TimeSpan.FromHours(2);
        _api.Put("a-order", "Delivered", Noon.AddHours(-2), "supplier-a");
        _api.Put("b-order", "Created", Noon.AddMinutes(-2), "supplier-b");
        _api.Respond = r => r.SupplierId == "supplier-a" && r.StartUtc == secondWindowEndA ? Json("not json") : null;

        var resultA = await RunAsync(_tenant);
        var resultB = await RunAsync(other);

        Assert.Equal(1, resultA.FailedConnections);
        Assert.Equal(0, resultB.FailedConnections);
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
        Assert.Equal(secondWindowEndA, await CheckpointAsync(_tenant));
        Assert.Equal(Noon, await CheckpointAsync(other));
    }

    [Fact]
    public async Task Logs_DoNotContainCredentialsCustomerDetailsOrProviderPayloads()
    {
        var checkpoint = Noon.AddHours(-2);
        await SeedConnectionAsync(_tenant, checkpoint);
        var firstWindowEnd = checkpoint - Overlap + TimeSpan.FromHours(1);
        _api.Put("logged-1", "Picking", checkpoint.AddMinutes(1));
        _api.Put("logged-2", "UnSupplied", checkpoint.AddMinutes(2));
        _api.Respond = r => r.StartUtc == firstWindowEnd ? Json(FakeTrendyolApi.TruncatedSensitiveBody) : null;

        var result = await RunAsync();

        Assert.Equal(1, result.FailedConnections);
        var entries = _syncLog.Entries.Concat(_clientLog.Entries).Concat(_mapperLog.Entries).ToList();
        Assert.Contains(entries, e => e.Level == LogLevel.Error && e.Message.Contains("WindowStartUtc=", StringComparison.Ordinal));
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
            _clientLog);

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

    private Task<OrderSyncCustomerResult> RunAsync(Guid? tenant = null) =>
        Sync().SyncCustomerWithResultAsync(tenant ?? _tenant, Ct);

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

    private static void AssertConsecutiveWindows(IReadOnlyList<ApiRequest> requests)
    {
        var windows = requests.Where(r => r.Page == 0).ToList();
        for (var i = 0; i < windows.Count; i++)
        {
            Assert.True(windows[i].EndMs > windows[i].StartMs);
            Assert.True(windows[i].EndUtc - windows[i].StartUtc <= TrendyolGoFoodPlatformClient.FetchWindowLength);
            if (i > 0)
                Assert.Equal(windows[i - 1].EndMs, windows[i].StartMs);
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
