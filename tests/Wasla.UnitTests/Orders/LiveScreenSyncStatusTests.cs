using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.GuidedSetup;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;
using Wasla.Web.Areas.Tenant.Controllers;
using Wasla.Web.GuidedSetup;
using Wasla.Web.Security;
using static Wasla.UnitTests.Admin.CentralAdminTenantOperationsServiceTests;
using static Wasla.UnitTests.GuidedSetup.GuidedSetupCoordinatorTests;

namespace Wasla.UnitTests.Orders;

/// <summary>
/// WAS-12: the Live Screen's connection indicator comes from the tenant's platform connection records, never from an
/// open page or a working poll. No connection: NotConnected. Order Sync off or every connection deactivated:
/// SyncDisabled. Live only when every active connection completed a sync within the threshold derived from its own
/// schedule and did not fail its last attempt. Otherwise Issue, also when one platform is healthy and another is not,
/// and when a connection has no failure on record but no successful sync either. Each tenant reads only its own records.
/// </summary>
public sealed class LiveScreenSyncStatusTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0, DateTimeKind.Utc);
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(135);

    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The rule -------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStaleThreshold_ComesFromTheConnectionsOwnScheduleAndTheWorkerCycle()
    {
        Assert.Equal(15, OrderSyncSchedule.WorkerCycleSeconds);
        // Three expected syncs, each the connection's interval plus one Worker cycle.
        Assert.Equal(DefaultStaleAfter, LiveScreenSyncRules.StaleAfter(30));
        Assert.Equal(TimeSpan.FromSeconds(3 * (60 + 15)), LiveScreenSyncRules.StaleAfter(60));
        // A missing or invalid interval still waits for the Worker cycle.
        Assert.Equal(TimeSpan.FromSeconds(45), LiveScreenSyncRules.StaleAfter(0));
        Assert.Equal(TimeSpan.FromSeconds(45), LiveScreenSyncRules.StaleAfter(-30));

        // The same age is still live for a connection that is scheduled less often.
        var age = TimeSpan.FromSeconds(200);
        Assert.Equal(LiveScreenSyncState.Issue, Evaluate(Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now - age)).State);
        Assert.Equal(LiveScreenSyncState.Live, Evaluate(Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now - age, intervalSeconds: 60)).State);

        // The Central Admin view keeps its own, longer presentation threshold.
        Assert.Equal(TimeSpan.FromMinutes(10), Wasla.Application.Admin.ProviderConnectionHealthRules.StaleAfter);
    }

    [Fact]
    public void NoConnection_IsNotConnected_WhateverTheOrderSyncSetting()
    {
        foreach (var syncOn in new[] { true, false })
        {
            var status = LiveScreenSyncRules.Evaluate([], syncOn, Now);
            Assert.Equal(LiveScreenSyncState.NotConnected, status.State);
            Assert.Null(status.LastSuccessfulSyncUtc);
            Assert.Empty(status.Platforms);
        }
    }

    [Fact]
    public void ADeactivatedConnection_IsSyncDisabled_NotLive()
    {
        var status = Evaluate(Connection(FoodPlatform.TrendyolYemek, lastSuccess: Now.AddSeconds(-10), isActive: false));

        Assert.Equal(LiveScreenSyncState.SyncDisabled, status.State);
        Assert.Equal([new LiveScreenPlatformSync(FoodPlatform.TrendyolYemek, LiveScreenSyncState.SyncDisabled)], status.Platforms);
        Assert.Null(status.LastSuccessfulSyncUtc);
    }

    [Fact]
    public void OrderSyncOff_IsSyncDisabled_EvenWhenTheConnectionsSyncedRecently()
    {
        var status = LiveScreenSyncRules.Evaluate(
            [Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20))],
            orderSyncEnabled: false,
            Now);

        Assert.Equal(LiveScreenSyncState.SyncDisabled, status.State);
        Assert.All(status.Platforms, platform => Assert.Equal(LiveScreenSyncState.SyncDisabled, platform.State));
        // Still the last time orders were synchronized.
        Assert.Equal(Now.AddSeconds(-20), status.LastSuccessfulSyncUtc);
    }

    [Fact]
    public void AConnectionThatSyncedWithinTheThreshold_IsLive_WithItsLastSuccessfulSync()
    {
        var lastSuccess = DateTime.SpecifyKind(Now.AddSeconds(-40), DateTimeKind.Unspecified);
        var status = Evaluate(Connection(FoodPlatform.Yemeksepeti, lastSuccess: lastSuccess, lastAttempt: lastSuccess));

        Assert.Equal(LiveScreenSyncState.Live, status.State);
        Assert.Equal(Now.AddSeconds(-40), status.LastSuccessfulSyncUtc);
        // Read from SQL Server the kind is Unspecified; the page must receive UTC.
        Assert.Equal(DateTimeKind.Utc, status.LastSuccessfulSyncUtc!.Value.Kind);

        Assert.Equal(LiveScreenSyncState.Live, Evaluate(Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now - DefaultStaleAfter)).State);
    }

    public static TheoryData<string, PlatformConnectionResult> Unhealthy => new()
    {
        // The last attempt failed (after the Worker's own retries), although the previous sync was recent.
        { "failing", Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-40), lastAttempt: Now.AddSeconds(-5), failures: 1) },
        { "paused by the circuit breaker", Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddMinutes(-3), failures: 5, circuitOpenUntil: Now.AddMinutes(4)) },
        // Attempts are recorded and no failure is, but nothing was ever synchronized: an attempt is not a success.
        { "attempted, never synced", Connection(FoodPlatform.Yemeksepeti, lastSuccess: null, lastAttempt: Now.AddSeconds(-5)) },
        { "no history at all", Connection(FoodPlatform.Yemeksepeti, lastSuccess: null, lastAttempt: null) },
        // Attempts keep starting but none completes (for example a fetch that never returns): no failure on record.
        { "stale with recent attempts", Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now - DefaultStaleAfter - TimeSpan.FromSeconds(1), lastAttempt: Now.AddSeconds(-5)) },
        // The Worker is not running: nothing is attempted and nothing fails.
        { "worker stopped", Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddHours(-2), lastAttempt: Now.AddHours(-2)) },
        // History is still being recovered after an outage: the checkpoint is behind.
        { "history behind", Connection(FoodPlatform.TrendyolYemek, lastSuccess: Now.AddHours(-5), lastAttempt: Now.AddSeconds(-10)) }
    };

    [Theory]
    [MemberData(nameof(Unhealthy))]
    public void ConnectionsWithoutRecentSuccess_AreAnIssue_NeverLive(string scenario, PlatformConnectionResult connection)
    {
        var status = Evaluate(connection);

        Assert.True(status.State == LiveScreenSyncState.Issue, scenario);
        Assert.Equal([new LiveScreenPlatformSync(connection.Platform, LiveScreenSyncState.Issue)], status.Platforms);
    }

    [Fact]
    public void MixedHealth_IsAnIssue_ThatNamesOnlyTheAffectedPlatform()
    {
        var status = Evaluate(
            Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20)),
            Connection(FoodPlatform.TrendyolYemek, lastSuccess: Now.AddHours(-3), failures: 4, circuitOpenUntil: Now.AddMinutes(10)),
            Connection(FoodPlatform.GetirYemek, lastSuccess: null, isActive: false));

        Assert.Equal(LiveScreenSyncState.Issue, status.State);
        Assert.Equal(
            [
                new LiveScreenPlatformSync(FoodPlatform.Yemeksepeti, LiveScreenSyncState.Live),
                new LiveScreenPlatformSync(FoodPlatform.TrendyolYemek, LiveScreenSyncState.Issue),
                new LiveScreenPlatformSync(FoodPlatform.GetirYemek, LiveScreenSyncState.SyncDisabled)
            ],
            status.Platforms);
        // Everything up to this instant came in from every active platform: the oldest, not the newest.
        Assert.Equal(Now.AddHours(-3), status.LastSuccessfulSyncUtc);
    }

    [Fact]
    public void ADeactivatedConnection_NeitherHidesNorSpoilsTheActiveOnes()
    {
        var live = Evaluate(
            Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20)),
            Connection(FoodPlatform.GetirYemek, lastSuccess: null, isActive: false));
        Assert.Equal(LiveScreenSyncState.Live, live.State);
        Assert.Equal(Now.AddSeconds(-20), live.LastSuccessfulSyncUtc);

        var issue = Evaluate(
            Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20), failures: 2),
            Connection(FoodPlatform.GetirYemek, lastSuccess: Now.AddSeconds(-20), isActive: false));
        Assert.Equal(LiveScreenSyncState.Issue, issue.State);
    }

    [Fact]
    public void TheLastSuccessfulSync_IsUnknown_WhileAnActiveConnectionHasNeverSynced()
    {
        var status = Evaluate(
            Connection(FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20)),
            Connection(FoodPlatform.TrendyolYemek, lastSuccess: null, lastAttempt: Now.AddSeconds(-5)));

        Assert.Equal(LiveScreenSyncState.Issue, status.State);
        Assert.Null(status.LastSuccessfulSyncUtc);
    }

    // The Live Screen snapshot --------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutAnyConnection_TheSnapshotSaysNotConnected_NotLive()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);

        var sync = (await LiveDataAsync(UserRole.Kitchen)).Sync;

        Assert.NotNull(sync);
        Assert.Equal(LiveScreenSyncState.NotConnected, sync!.State);
        Assert.Empty(sync.Platforms);
    }

    [Fact]
    public async Task TheSnapshotFollowsTheStoredConnectionRecords_OnEveryPoll()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var id = await SeedConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, lastSuccess: null, lastAttempt: Now.AddSeconds(-5));

        // Attempted but never synchronized: not live.
        Assert.Equal(LiveScreenSyncState.Issue, (await LiveDataAsync(UserRole.Kitchen)).Sync!.State);

        await UpdateConnectionAsync(_tenant, id, c => { c.LastSuccessfulSync = Now.AddSeconds(-15); c.LastSyncAttempt = Now.AddSeconds(-15); });
        var live = (await LiveDataAsync(UserRole.Kitchen)).Sync!;
        Assert.Equal(LiveScreenSyncState.Live, live.State);
        Assert.Equal(Now.AddSeconds(-15), live.LastSuccessfulSyncUtc);

        await UpdateConnectionAsync(_tenant, id, c => { c.ConsecutiveFailures = 1; c.LastSyncAttempt = Now.AddSeconds(-2); });
        Assert.Equal(LiveScreenSyncState.Issue, (await LiveDataAsync(UserRole.Kitchen)).Sync!.State);

        await UpdateConnectionAsync(_tenant, id, c => c.IsActive = false);
        Assert.Equal(LiveScreenSyncState.SyncDisabled, (await LiveDataAsync(UserRole.Kitchen)).Sync!.State);
    }

    [Fact]
    public async Task OrderSyncSwitchedOff_IsSyncDisabled()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await SeedConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-10));
        await using (var db = await _tenants.CreateAsync(_tenant, Ct))
        {
            var settings = await db.TenantOperationalSettings.SingleAsync(Ct);
            settings.OrderSyncEnabled = false;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(LiveScreenSyncState.SyncDisabled, (await LiveDataAsync(UserRole.Kitchen)).Sync!.State);
    }

    /// <summary>
    /// Every Live Screen role sees the same read-only status, and only the state, the oldest last successful sync and
    /// each platform's state travel: no store id, credential, failure count, attempt time or error text.
    /// </summary>
    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Kitchen)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.Viewer)]
    public async Task EveryLiveScreenRole_GetsTheSameReadOnlyStatus(UserRole role)
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await _tenants.SeedUserAsync(_tenant, _user, role);
        await SeedConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20), storeId: "YS-STORE-4471");
        await SeedConnectionAsync(_tenant, FoodPlatform.TrendyolYemek, lastSuccess: Now.AddHours(-1), failures: 3, storeId: "TY-STORE-9902");

        var snapshot = await LiveDataAsync(role);
        var json = JsonSerializer.Serialize(snapshot, Web);
        using var document = JsonDocument.Parse(json);
        var sync = document.RootElement.GetProperty("sync");

        Assert.Equal("Issue", sync.GetProperty("state").GetString());
        Assert.Equal("2026-10-13T08:30:00Z", sync.GetProperty("lastSuccessfulSyncUtc").GetString());
        Assert.Equal(["lastSuccessfulSyncUtc", "platforms", "state"], sync.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Yemeksepeti:Live", "TrendyolYemek:Issue"],
            sync.GetProperty("platforms").EnumerateArray()
                .Select(p => p.GetProperty("platform").GetString() + ":" + p.GetProperty("state").GetString()));
        Assert.All(sync.GetProperty("platforms").EnumerateArray(),
            platform => Assert.Equal(["platform", "state"], platform.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        foreach (var forbidden in new[] { "STORE-", "storeId", "consecutiveFailures", "circuit", "lastSyncAttempt", "secret", "apiKey", "token", "connection" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EachTenantReadsOnlyItsOwnConnections()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        await _tenants.SeedSettingsAsync(_otherTenant, TenantOperationalMode.Live);
        await SeedConnectionAsync(_tenant, FoodPlatform.Yemeksepeti, lastSuccess: Now.AddSeconds(-20));
        await SeedConnectionAsync(_otherTenant, FoodPlatform.TrendyolYemek, lastSuccess: Now.AddHours(-1), failures: 5, circuitOpenUntil: Now.AddMinutes(5));

        var mine = (await LiveDataAsync(UserRole.Kitchen, _tenant)).Sync!;
        var theirs = (await LiveDataAsync(UserRole.Kitchen, _otherTenant)).Sync!;

        Assert.Equal(LiveScreenSyncState.Live, mine.State);
        Assert.Equal([new LiveScreenPlatformSync(FoodPlatform.Yemeksepeti, LiveScreenSyncState.Live)], mine.Platforms);
        Assert.Equal(LiveScreenSyncState.Issue, theirs.State);
        Assert.Equal([new LiveScreenPlatformSync(FoodPlatform.TrendyolYemek, LiveScreenSyncState.Issue)], theirs.Platforms);

        // The connections are read for the resolved tenant only; the action takes no tenant from the request.
        var connections = new FakePlatformConnections();
        await LiveDataAsync(UserRole.Kitchen, _tenant, connections);
        Assert.Equal([_tenant], connections.ReadTenants);
        Assert.DoesNotContain(typeof(OrdersController).GetMethod(nameof(OrdersController.LiveData))!.GetParameters(),
            parameter => parameter.ParameterType == typeof(Guid) || parameter.ParameterType == typeof(Guid?) || parameter.ParameterType == typeof(string));
    }

    [Fact]
    public async Task WhenTheStatusCannotBeRead_SyncIsNull_AndTheOrdersStillArrive()
    {
        await _tenants.SeedSettingsAsync(_tenant, TenantOperationalMode.Live);
        var order = await SeedOrderAsync();

        var failingConnections = await LiveDataAsync(UserRole.Kitchen, connections: new FakePlatformConnections { FailReads = true });
        Assert.Null(failingConnections.Sync);
        Assert.NotNull(failingConnections.Automation);
        Assert.Contains(failingConnections.Orders, o => o.Id == order);

        // Without the Order Sync setting the state cannot be told, so the connections are not even read.
        var connections = new FakePlatformConnections();
        var failingSettings = await LiveDataAsync(UserRole.Kitchen, connections: connections, modes: new FakeTenantModes { FailReads = true });
        Assert.Null(failingSettings.Sync);
        Assert.Empty(connections.ReadTenants);
        Assert.Contains(failingSettings.Orders, o => o.Id == order);
    }

    // Copy and markup ------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryCulture_HasItsOwnWordsForEachState_AndTheViewNeverRendersLiveStatically()
    {
        var keys = new[]
        {
            "Orders.LiveDisplay.LiveStatus",
            "Orders.LiveDisplay.SyncStatus.NotConnected",
            "Orders.LiveDisplay.SyncStatus.SyncDisabled",
            "Orders.LiveDisplay.SyncStatus.Issue",
            "Orders.LiveDisplay.SyncStatus.IssueWithPlatforms",
            "Orders.LiveDisplay.LastSync"
        };
        foreach (var culture in new[] { "", ".tr-TR", ".en-US", ".ar-SA", ".ru-RU" })
        {
            var values = Resources(culture);
            foreach (var key in keys)
                Assert.False(string.IsNullOrWhiteSpace(values.GetValueOrDefault(key)), $"{key} is missing in SharedResource{culture}.resx");
            // Four states, four different phrases: the meaning never depends on the color.
            Assert.Equal(4, keys.Take(4).Select(key => values[key]).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("{0}", values["Orders.LiveDisplay.SyncStatus.IssueWithPlatforms"], StringComparison.Ordinal);
            Assert.StartsWith(values["Orders.LiveDisplay.SyncStatus.Issue"], values["Orders.LiveDisplay.SyncStatus.IssueWithPlatforms"], StringComparison.Ordinal);
        }
        foreach (var culture in new[] { ".en-US", ".ar-SA", ".ru-RU" })
            Assert.NotEqual(Resources(".tr-TR")["Orders.LiveDisplay.SyncStatus.Issue"], Resources(culture)["Orders.LiveDisplay.SyncStatus.Issue"]);

        var view = File.ReadAllText(RepoFile("src", "Wasla.Web", "Areas", "Tenant", "Views", "Orders", "LiveDisplay.cshtml"));
        var badge = System.Text.RegularExpressions.Regex.Match(view, "<span id=\"ordersLiveDisplaySyncStatus\"[\\s\\S]*?</span>\\s*</span>").Value;
        Assert.Contains("role=\"status\"", badge, StringComparison.Ordinal);
        Assert.Contains("data-sync-state=\"pending\"", badge, StringComparison.Ordinal);
        Assert.Contains("@L[\"Common.Loading\"]", badge, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveStatus", badge, StringComparison.Ordinal);
        Assert.Contains("orders-platform-sync-status.js", view, StringComparison.Ordinal);
        foreach (var (message, key) in new[]
                 {
                     ("syncStatusLive", "Orders.LiveDisplay.LiveStatus"),
                     ("syncStatusNotConnected", "Orders.LiveDisplay.SyncStatus.NotConnected"),
                     ("syncStatusSyncDisabled", "Orders.LiveDisplay.SyncStatus.SyncDisabled"),
                     ("syncStatusIssue", "Orders.LiveDisplay.SyncStatus.Issue"),
                     ("syncStatusIssueWithPlatforms", "Orders.LiveDisplay.SyncStatus.IssueWithPlatforms")
                 })
            Assert.Contains($"[\"{message}\"] = L[\"{key}\"].Value", view, StringComparison.Ordinal);
        Assert.Contains("@L[\"Orders.LiveDisplay.LastSync\"] <time id=\"ordersLiveDisplayLastSync\"></time>", view, StringComparison.Ordinal);
    }

    public void Dispose() => _tenants.Dispose();

    // Helpers --------------------------------------------------------------------------------------------------------

    private static LiveScreenSyncStatus Evaluate(params PlatformConnectionResult[] connections) =>
        LiveScreenSyncRules.Evaluate(connections, orderSyncEnabled: true, Now);

    private static PlatformConnectionResult Connection(
        FoodPlatform platform,
        DateTime? lastSuccess,
        DateTime? lastAttempt = null,
        int failures = 0,
        DateTime? circuitOpenUntil = null,
        bool isActive = true,
        int intervalSeconds = 30) =>
        new(Guid.NewGuid(), platform, "store", isActive, lastAttempt ?? lastSuccess, lastSuccess, failures, circuitOpenUntil, intervalSeconds);

    private async Task<Guid> SeedConnectionAsync(
        Guid tenantId,
        FoodPlatform platform,
        DateTime? lastSuccess,
        DateTime? lastAttempt = null,
        int failures = 0,
        DateTime? circuitOpenUntil = null,
        string storeId = "store")
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        var connection = new PlatformConnection
        {
            Platform = platform,
            StoreId = storeId,
            EncryptedApiKey = "fake-encrypted-key",
            EncryptedApiSecret = "fake-encrypted-secret",
            IsActive = true,
            LastSuccessfulSync = lastSuccess,
            LastSyncAttempt = lastAttempt ?? lastSuccess,
            ConsecutiveFailures = failures,
            CircuitOpenUntil = circuitOpenUntil
        };
        db.PlatformConnections.Add(connection);
        await db.SaveChangesAsync(Ct);
        return connection.Id;
    }

    private async Task UpdateConnectionAsync(Guid tenantId, Guid id, Action<PlatformConnection> change)
    {
        await using var db = await _tenants.CreateAsync(tenantId, Ct);
        change(await db.PlatformConnections.SingleAsync(c => c.Id == id, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> SeedOrderAsync()
    {
        var id = Guid.NewGuid();
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        db.Orders.Add(new Order
        {
            Id = id,
            Platform = FoodPlatform.Yemeksepeti,
            ExternalOrderId = "sync-status",
            ExternalOrderCode = "sync-status",
            IdempotencyKey = "sync-status",
            InternalStatus = OrderStatus.New,
            PlatformStatus = "Created",
            CustomerName = "Test Customer",
            CustomerPhone = "+905550000000",
            CustomerAddress = "Test Address",
            TotalAmount = 100m,
            PaymentMethod = PaymentMethod.CreditCard,
            PaymentStatus = PaymentStatus.Paid,
            CreatedAtPlatform = Now.AddMinutes(-5),
            ReceivedAt = Now.AddMinutes(-5)
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<LiveScreenSnapshotResult> LiveDataAsync(
        UserRole role,
        Guid? tenantId = null,
        IPlatformConnectionService? connections = null,
        ITenantOperationalModeService? modes = null)
    {
        var tenant = tenantId ?? _tenant;
        var coordinator = new GuidedSetupCoordinator(new GuidedSetupService(_tenants, TimeProvider.System),
            new TenantNavigationAuthorizationService(RealPolicies(tenant)), new FakeSetupStatus(), new FakeDemos(),
            new FakePrintBridgeDevices(), new CapturingLogger());
        var controller = new OrdersController(
            new FixedTenant(tenant),
            new OrderReadService(_tenants, new FixedTime(Now), NullLogger<OrderReadService>.Instance),
            actions: null!,
            orderSyncSettings: null!,
            orderSettings: null!,
            receiptCreation: null!,
            manualPrint: null!,
            demos: new FakeDemos(),
            authorization: RealPolicies(tenant),
            orderSettingsValidator: null!,
            logger: NullLogger<OrdersController>.Instance,
            localizer: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(role, tenant) }
            }
        };

        var ok = Assert.IsType<OkObjectResult>(await controller.LiveData(
            coordinator,
            modes ?? new TenantOperationalModeService(_tenants),
            connections ?? new PlatformConnectionService(_tenants, null!, null!),
            Ct));
        return Assert.IsType<LiveScreenSnapshotResult>(ok.Value);
    }

    private ClaimsPrincipal Principal(UserRole role, Guid tenantId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _user.ToString()),
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            authenticationType: "Tenant"));

    private static Dictionary<string, string> Resources(string culture) =>
        XDocument.Load(RepoFile("src", "Wasla.Web", "Resources", $"SharedResource{culture}.resx"))
            .Root!
            .Elements("data")
            .Where(element => element.Attribute("name") is not null)
            .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")?.Value ?? string.Empty, StringComparer.Ordinal);

    private static string RepoFile(params string[] segments) =>
        Wasla.UnitTests.Admin.TenantOperationsRulesTests.RepoFile(segments);
}

/// <summary>A platform connection reader that records which tenant it was asked for, and can fail like a database.</summary>
internal sealed class FakePlatformConnections : IPlatformConnectionService
{
    public IReadOnlyList<PlatformConnectionResult> Connections { get; set; } = [];

    public bool FailReads { get; set; }

    public List<Guid> ReadTenants { get; } = new();

    public Task<IReadOnlyList<PlatformConnectionResult>> GetListAsync(Guid customerId, CancellationToken ct)
    {
        ReadTenants.Add(customerId);
        if (FailReads)
            throw new InvalidOperationException("database unavailable");
        return Task.FromResult(Connections);
    }

    public Task<PlatformConnectionDetailResult?> GetByIdAsync(Guid customerId, Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<CreatePlatformConnectionResult> CreateAsync(Guid customerId, CreatePlatformConnectionCommand command, CancellationToken ct) => throw new NotSupportedException();

    public Task<CreatePlatformConnectionResult> UpdateAsync(Guid customerId, Guid id, UpdatePlatformConnectionCommand command, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> SetActiveAsync(Guid customerId, Guid id, bool isActive, CancellationToken ct) => throw new NotSupportedException();
}
