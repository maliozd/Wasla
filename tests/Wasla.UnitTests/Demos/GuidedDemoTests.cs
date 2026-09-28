using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Demos;
using Wasla.Application.Orders;
using Wasla.Application.Tours;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Persistence.Tenant.Configurations;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Demos;

public sealed class GuidedDemoScenarioTests
{
    [Fact]
    public void SushiCode_UsesRollsAndEdamame()
    {
        var scenario = GuidedDemoScenarioCatalog.ForSubtypes(["sushi"]);
        Assert.Equal("sushi", scenario.Code);
        Assert.Contains(scenario.Lines, line => line.NameKey == "Demo.Sushi.Edamame");
    }

    [Fact]
    public void FirstSelectedCode_Wins()
    {
        var scenario = GuidedDemoScenarioCatalog.ForSubtypes(["burger", "sushi"]);
        Assert.Equal("burger", scenario.Code);
        Assert.Contains(scenario.Lines, line => line.NameKey == "Demo.Burger.Burger");
    }

    [Fact]
    public void FirstMatchingSubtypeWins()
    {
        var scenario = GuidedDemoScenarioCatalog.ForSubtypes(["cafe"]);
        Assert.Equal("cafe", scenario.Code);
        Assert.Contains(scenario.Lines, line => line.NameKey == "Demo.Cafe.Coffee");
    }

    [Fact]
    public void UnknownSelection_UsesTheLokantaBasket()
    {
        var scenario = GuidedDemoScenarioCatalog.ForSubtypes(["other"]);
        Assert.Equal("lokanta", scenario.Code);
    }
}

public sealed class GuidedDemoTransitionTests
{
    [Fact]
    public void HappyPath_MovesThroughPreparation()
    {
        Assert.True(GuidedDemoTransitions.TryMove(OrderStatus.New, "approve", out var accepted, out _));
        Assert.Equal(OrderStatus.Accepted, accepted);
        Assert.True(GuidedDemoTransitions.TryMove(accepted, "start-preparing", out var preparing, out _));
        Assert.Equal(OrderStatus.Preparing, preparing);
        Assert.True(GuidedDemoTransitions.TryMove(preparing, "mark-ready", out var ready, out _));
        Assert.Equal(OrderStatus.ReadyForPickup, ready);
    }

    [Theory]
    [InlineData(OrderStatus.ReadyForPickup, "hand-to-courier")]
    [InlineData(OrderStatus.OnTheWay, "mark-delivered")]
    public void UserCannotDoTheCouriersSteps(OrderStatus current, string action)
    {
        Assert.False(GuidedDemoTransitions.TryMove(current, action, out var next, out var key));
        Assert.Equal(current, next);
        Assert.Equal("Orders.InvalidStatusForAction", key);
    }

    [Fact]
    public void InvalidTransition_IsRejected()
    {
        Assert.False(GuidedDemoTransitions.TryMove(OrderStatus.New, "mark-ready", out var next, out var key));
        Assert.Equal(OrderStatus.New, next);
        Assert.Equal("Orders.InvalidStatusForAction", key);
    }
}

public sealed class GuidedDemoServiceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly TenantSqlite _tenants = new();
    private readonly Clock _clock = new();

    [Fact]
    public async Task Start_IsIsolatedAndDoesNotCreateAProductionOrder()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        await SeedAsync(_tenantB, _userA);
        var service = CreateService(["sushi"]);

        var created = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        Assert.Equal("sushi", created.ScenarioCode);
        Assert.Equal(OrderStatus.New, created.Status);
        Assert.NotNull(await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None));
        Assert.Null(await service.GetActiveAsync(_tenantA, _userB, CancellationToken.None));
        Assert.Null(await service.GetActiveAsync(_tenantB, _userA, CancellationToken.None));
        Assert.Equal(1, await CountSessionsAsync(_tenantA));
        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        Assert.Empty(await db.Orders.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.PrintJobs.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OtherUser_CannotMoveTheDemo()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        var service = CreateService(["burger"]);
        var created = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        var denied = await service.ApplyActionAsync(_tenantA, _userB, created.Id, "approve", CancellationToken.None);

        Assert.False(denied.Succeeded);
        Assert.Equal(OrderStatus.New, (await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Lifecycle_RejectsAnInvalidSecondApprove()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(null);
        var created = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        Assert.True((await service.ApplyActionAsync(_tenantA, _userA, created.Id, "approve", CancellationToken.None)).Succeeded);
        var again = await service.ApplyActionAsync(_tenantA, _userA, created.Id, "approve", CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Equal(OrderStatus.Accepted, (await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ExpiredDemo_IsNotShown()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(["cafe"]);
        await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        _clock.Now = _clock.Now.AddHours(3);

        Assert.Null(await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None));
    }

    [Fact]
    public void Viewer_DoesNotReceiveTheInteractiveDemoTour()
    {
        Assert.Empty(ProductTourCatalog.StepsFor(ProductTourKeys.GuidedDemo, UserRole.Viewer));
        Assert.Equal("demo-start", ProductTourCatalog.StepsFor(ProductTourKeys.GuidedDemo, UserRole.Owner)[0].PrimaryAction);
    }

    [Fact]
    public async Task Delivery_CompletesOnlyTheDemo_AndReplayRequiresStart()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(["pizza"]);
        var created = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        Assert.Equal(created.Id, (await service.StartAsync(_tenantA, _userA, CancellationToken.None)).Id);
        await MarkReadyAsync(service, created.Id);
        Assert.Equal(OrderStatus.ReadyForPickup, (await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None))!.Status);
        Assert.False((await service.ApplyActionAsync(_tenantA, _userA, created.Id, "hand-to-courier", CancellationToken.None)).Succeeded);
        var simulator = CreateSimulator();

        _clock.Now = _clock.Now.AddSeconds(10);
        Assert.Equal(1, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));
        Assert.Equal(OrderStatus.OnTheWay, (await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None))!.Status);
        Assert.False((await service.ApplyActionAsync(_tenantA, _userA, created.Id, "mark-delivered", CancellationToken.None)).Succeeded);

        _clock.Now = _clock.Now.AddSeconds(10);
        Assert.Equal(1, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));

        Assert.Null(await service.GetActiveAsync(_tenantA, _userA, CancellationToken.None));
        var shown = await service.GetForLiveScreenAsync(_tenantA, _userA, CancellationToken.None);
        Assert.Equal(OrderStatus.Delivered, shown!.Status);
        Assert.Equal(_clock.Now.UtcDateTime, shown.DeliveredAtUtc);
        Assert.False((await service.ApplyActionAsync(_tenantA, _userA, created.Id, "approve", CancellationToken.None)).Succeeded);
        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        Assert.Empty(await db.Orders.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.PrintJobs.ToListAsync(TestContext.Current.CancellationToken));
        var replay = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        Assert.NotEqual(created.Id, replay.Id);
        Assert.Equal(OrderStatus.New, replay.Status);
    }

    [Fact]
    public async Task AnotherTenant_AndExpiredSession_CannotBeAdvanced()
    {
        await SeedAsync(_tenantA, _userA);
        await SeedAsync(_tenantB, _userA);
        var service = CreateService(null);
        var created = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        Assert.False((await service.ApplyActionAsync(_tenantB, _userA, created.Id, "approve", CancellationToken.None)).Succeeded);
        _clock.Now = _clock.Now.AddHours(3);
        Assert.False((await service.ApplyActionAsync(_tenantA, _userA, created.Id, "approve", CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Courier_PicksUpThenDelivers_OneStepPerDelay_AndOnlyEligibleDemos()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        var service = CreateService(null);
        var ready = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        await MarkReadyAsync(service, ready.Id);
        var accepted = await service.StartAsync(_tenantA, _userB, CancellationToken.None);
        Assert.True((await service.ApplyActionAsync(_tenantA, _userB, accepted.Id, "approve", CancellationToken.None)).Succeeded);
        var simulator = CreateSimulator();

        Assert.Equal(0, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));

        _clock.Now = _clock.Now.Add(GuidedDemoDeliverySimulator.CourierDelay);
        Assert.Equal(1, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));
        Assert.Equal(OrderStatus.OnTheWay, await ReadStatusAsync(ready.Id));
        Assert.Equal(0, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));

        _clock.Now = _clock.Now.Add(GuidedDemoDeliverySimulator.CourierDelay);
        Assert.Equal(1, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));
        Assert.Equal(OrderStatus.Delivered, await ReadStatusAsync(ready.Id));
        Assert.Equal(OrderStatus.Accepted, await ReadStatusAsync(accepted.Id));
    }

    [Fact]
    public async Task Courier_IsIdempotentAcrossRepeatedCyclesAndWorkerInstances()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(null);
        var demo = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        await MarkReadyAsync(service, demo.Id);
        var firstWorker = CreateSimulator();
        var secondWorker = CreateSimulator();

        _clock.Now = _clock.Now.AddSeconds(30);
        var pickup = new[]
        {
            await firstWorker.AdvanceDueAsync(_tenantA, CancellationToken.None),
            await secondWorker.AdvanceDueAsync(_tenantA, CancellationToken.None),
            await firstWorker.AdvanceDueAsync(_tenantA, CancellationToken.None)
        };
        Assert.Equal(new[] { 1, 0, 0 }, pickup);
        Assert.Equal(OrderStatus.OnTheWay, await ReadStatusAsync(demo.Id));

        _clock.Now = _clock.Now.AddSeconds(30);
        var delivery = new[]
        {
            await secondWorker.AdvanceDueAsync(_tenantA, CancellationToken.None),
            await firstWorker.AdvanceDueAsync(_tenantA, CancellationToken.None),
            await secondWorker.AdvanceDueAsync(_tenantA, CancellationToken.None)
        };
        Assert.Equal(new[] { 1, 0, 0 }, delivery);

        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        var row = await db.GuidedDemoSessions.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Delivered, row.Status);
        Assert.Equal(_clock.Now.UtcDateTime, row.CompletedAtUtc);
    }

    [Fact]
    public async Task Courier_IgnoresCancelledExpiredAndFinishedDemos()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        await SeedAsync(_tenantB, _userA);
        var service = CreateService(null);

        var finished = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        await MarkReadyAsync(service, finished.Id, _tenantA);
        await service.FinishActiveAsync(_tenantA, _userA, CancellationToken.None);

        var cancelled = await service.StartAsync(_tenantA, _userB, CancellationToken.None);
        Assert.True((await service.ApplyActionAsync(_tenantA, _userB, cancelled.Id, "reject", CancellationToken.None)).Succeeded);

        var expired = await service.StartAsync(_tenantB, _userA, CancellationToken.None);
        await MarkReadyAsync(service, expired.Id, _tenantB);
        _clock.Now = _clock.Now.AddHours(3);

        var simulator = CreateSimulator();
        Assert.Equal(0, await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None));
        Assert.Equal(0, await simulator.AdvanceDueAsync(_tenantB, CancellationToken.None));
        Assert.Equal(OrderStatus.ReadyForPickup, await ReadStatusAsync(finished.Id, _tenantA));
        Assert.Equal(OrderStatus.Cancelled, await ReadStatusAsync(cancelled.Id, _tenantA));
        Assert.Equal(OrderStatus.ReadyForPickup, await ReadStatusAsync(expired.Id, _tenantB));
    }

    [Fact]
    public async Task DeliveredDemo_StaysOnTheOwnersLiveScreenBriefly_AndNeverTouchesOrdersOrPrintJobs()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        var service = CreateService(null);
        var demo = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        await MarkReadyAsync(service, demo.Id);
        var simulator = CreateSimulator();
        _clock.Now = _clock.Now.AddSeconds(10);
        await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None);
        _clock.Now = _clock.Now.AddSeconds(10);
        await simulator.AdvanceDueAsync(_tenantA, CancellationToken.None);
        Assert.Equal(OrderStatus.Delivered, await ReadStatusAsync(demo.Id));

        Assert.Equal(demo.Id, (await service.GetForLiveScreenAsync(_tenantA, _userA, CancellationToken.None))!.Id);
        Assert.Null(await service.GetForLiveScreenAsync(_tenantA, _userB, CancellationToken.None));
        await using (var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None))
        {
            Assert.Empty(await db.Orders.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await db.PrintJobs.ToListAsync(TestContext.Current.CancellationToken));
        }

        // The same canonical window as real delivered orders on the Live Screen.
        _clock.Now = _clock.Now.Add(LiveScreenVisibility.RecentDeliveredWindow).AddSeconds(-1);
        Assert.NotNull(await service.GetForLiveScreenAsync(_tenantA, _userA, CancellationToken.None));
        _clock.Now = _clock.Now.AddSeconds(2);
        Assert.Null(await service.GetForLiveScreenAsync(_tenantA, _userA, CancellationToken.None));
    }

    /// <summary>The restaurant's last demo action is Mark ready; the courier steps follow on their own.</summary>
    private async Task MarkReadyAsync(GuidedDemoService service, Guid demoId, Guid? tenantId = null)
    {
        foreach (var action in new[] { "approve", "start-preparing", "mark-ready" })
            Assert.True((await service.ApplyActionAsync(tenantId ?? _tenantA, _userA, demoId, action, CancellationToken.None)).Succeeded);
    }

    private GuidedDemoDeliverySimulator CreateSimulator() => new(_tenants, _clock);

    private async Task<OrderStatus> ReadStatusAsync(Guid demoId, Guid? tenantId = null)
    {
        await using var db = await _tenants.CreateAsync(tenantId ?? _tenantA, CancellationToken.None);
        return await db.GuidedDemoSessions.Where(row => row.Id == demoId).Select(row => row.Status).SingleAsync();
    }

    [Fact]
    public async Task SecondStart_ReturnsTheSameOpenSession()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(null);

        var first = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        var second = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountOpenSessionsAsync(_tenantA, _userA));
    }

    [Fact]
    public async Task ExpiredOpenSession_IsClosedAndDoesNotBlockANewStart()
    {
        await SeedAsync(_tenantA, _userA);
        var service = CreateService(null);
        var expired = await service.StartAsync(_tenantA, _userA, CancellationToken.None);
        _clock.Now = _clock.Now.AddHours(3);

        var fresh = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        Assert.NotEqual(expired.Id, fresh.Id);
        Assert.Equal(1, await CountOpenSessionsAsync(_tenantA, _userA));
        await using var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        Assert.NotNull((await db.GuidedDemoSessions.SingleAsync(row => row.Id == expired.Id, TestContext.Current.CancellationToken)).CompletedAtUtc);
    }

    [Fact]
    public async Task ConcurrentStart_ThatLosesTheInsertRace_ReturnsTheWinningSession()
    {
        await SeedAsync(_tenantA, _userA);
        var winnerId = Guid.NewGuid();
        // Runs after the service found no active demo and before it inserts, like a second tab.
        var service = new GuidedDemoService(_tenants, new FixedSubtypes(null, async () =>
        {
            await using var competing = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
            competing.GuidedDemoSessions.Add(OpenSession(winnerId, _userA));
            await competing.SaveChangesAsync();
        }), _clock);

        var result = await service.StartAsync(_tenantA, _userA, CancellationToken.None);

        Assert.Equal(winnerId, result.Id);
        Assert.Equal(1, await CountOpenSessionsAsync(_tenantA, _userA));
    }

    [Fact]
    public async Task Database_RejectsASecondOpenSessionForTheSameUser()
    {
        await SeedAsync(_tenantA, _userA, _userB);
        await using (var db = await _tenants.CreateAsync(_tenantA, CancellationToken.None))
        {
            db.GuidedDemoSessions.Add(OpenSession(Guid.NewGuid(), _userA));
            db.GuidedDemoSessions.Add(OpenSession(Guid.NewGuid(), _userB));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicate = await _tenants.CreateAsync(_tenantA, CancellationToken.None);
        duplicate.GuidedDemoSessions.Add(OpenSession(Guid.NewGuid(), _userA));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.True(GuidedDemoService.IsOpenSessionConflict(error));
    }

    [Fact]
    public void OpenSessionConflict_RecognizesSqlServerAndIgnoresOtherErrors()
    {
        var sqlServer = new DbUpdateException("update failed", new Exception(
            "Cannot insert duplicate key row in object 'dbo.GuidedDemoSessions' with unique index 'IX_GuidedDemoSessions_UserId_Open'. The duplicate key value is (x)."));
        var other = new DbUpdateException("update failed", new Exception(
            "Cannot insert duplicate key row in object 'dbo.PlatformConnections' with unique index 'IX_PlatformConnections_Platform'."));

        Assert.True(GuidedDemoService.IsOpenSessionConflict(sqlServer));
        Assert.False(GuidedDemoService.IsOpenSessionConflict(other));
    }

    private GuidedDemoSession OpenSession(Guid id, Guid userId) => new()
    {
        Id = id,
        UserId = userId,
        ScenarioCode = "lokanta",
        Status = OrderStatus.New,
        CustomerNameKey = "Demo.Customer",
        ItemsJson = "[]",
        ReceivedAtUtc = _clock.Now.UtcDateTime,
        ExpiresAtUtc = _clock.Now.UtcDateTime.AddHours(2)
    };

    private async Task<int> CountOpenSessionsAsync(Guid tenantId, Guid userId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        return await db.GuidedDemoSessions.CountAsync(row => row.UserId == userId && row.CompletedAtUtc == null);
    }

    private GuidedDemoService CreateService(IReadOnlyList<string>? codes) =>
        new(_tenants, new FixedSubtypes(codes), _clock);

    private async Task SeedAsync(Guid tenantId, params Guid[] userIds)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        foreach (var userId in userIds)
        {
            if (await db.AppUsers.AnyAsync(user => user.Id == userId))
                continue;
            db.AppUsers.Add(new AppUser
            {
                Id = userId,
                Email = userId.ToString("N") + "@example.test",
                PasswordHash = "hash",
                FullName = "Demo User",
                Role = UserRole.Owner,
                IsActive = true
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<int> CountSessionsAsync(Guid tenantId)
    {
        await using var db = await _tenants.CreateAsync(tenantId, CancellationToken.None);
        return await db.GuidedDemoSessions.CountAsync();
    }

    public void Dispose() => _tenants.Dispose();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedSubtypes : ITenantBusinessSubtypeReader
    {
        private readonly IReadOnlyList<string>? _codes;
        private readonly Func<Task>? _beforeReturn;

        public FixedSubtypes(IReadOnlyList<string>? codes, Func<Task>? beforeReturn = null)
        {
            _codes = codes;
            _beforeReturn = beforeReturn;
        }

        public async Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct)
        {
            if (_beforeReturn is not null)
                await _beforeReturn();
            return _codes;
        }
    }

    private sealed class TenantSqlite : ITenantDbContextFactory, IDisposable
    {
        private readonly Dictionary<Guid, SqliteConnection> _connections = new();

        public Task<TenantDbContext> CreateAsync(Guid customerId, CancellationToken ct)
        {
            if (!_connections.TryGetValue(customerId, out var connection))
            {
                connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();
                _connections.Add(customerId, connection);
                using var setup = new DemoTenantDbContext(Options(connection));
                setup.Database.EnsureCreated();
            }

            return Task.FromResult<TenantDbContext>(new DemoTenantDbContext(Options(connection)));
        }

        public void Dispose()
        {
            foreach (var connection in _connections.Values)
                connection.Dispose();
        }

        private static DbContextOptions<TenantDbContext> Options(SqliteConnection connection) =>
            new DbContextOptionsBuilder<TenantDbContext>().UseSqlite(connection).Options;
    }

    private sealed class DemoTenantDbContext : TenantDbContext
    {
        public DemoTenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppUser>(builder =>
            {
                builder.ToTable("AppUsers");
                builder.HasKey(user => user.Id);
                builder.Property(user => user.Email).IsRequired();
                builder.Property(user => user.PasswordHash).IsRequired();
            });
            // The real configuration, so the unique open-session index is exercised.
            modelBuilder.ApplyConfiguration(new GuidedDemoSessionConfiguration());
        }
    }
}
