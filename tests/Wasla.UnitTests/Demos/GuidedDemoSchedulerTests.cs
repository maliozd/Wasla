using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wasla.Application.Abstractions.Signup;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Services;
using Wasla.UnitTests.Setup;

namespace Wasla.UnitTests.Demos;

/// <summary>
/// Wasla.Worker's guided-demo scheduler: it wakes at a practice order's persisted stage deadline (not on the 15-second
/// order-sync cycle), moves it once with the existing conditional update, recovers after a restart, stays safe with
/// several Worker instances, and costs nothing while no practice order is open. Real services on SQLite with the
/// production model; time is a manual clock, so nothing waits 20 seconds.
/// </summary>
public sealed class GuidedDemoSchedulerTests : IDisposable
{
    private readonly OperationalModeTestDatabases _tenants = new();
    private readonly ManualTime _time = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNextLook_IsTheStageDeadline_FromThePersistedTimestamp_OrSoonWhileTheUserStillHasSteps()
    {
        await _tenants.SeedUserAsync(_tenant, _owner);
        var simulator = Simulator();

        var before = _tenants.StatementCount;
        Assert.Equal(new GuidedDemoAdvanceResult(0, null), await simulator.AdvanceDueAndPlanAsync(_tenant, Ct));
        Assert.Equal(1, _tenants.StatementCount - before);

        var demos = Demos();
        var demo = await demos.StartAsync(_tenant, _owner, Ct);
        var preparing = await simulator.AdvanceDueAndPlanAsync(_tenant, Ct);
        Assert.Equal(_time.UtcNow + GuidedDemoTiming.UserStepCheckInterval, preparing.NextCheckAtUtc);

        foreach (var action in new[] { "approve", "start-preparing", "mark-ready" })
            Assert.True((await demos.ApplyActionAsync(_tenant, _owner, demo.Id, action, Ct)).Succeeded);
        var readyAt = _time.UtcNow;
        _time.Advance(TimeSpan.FromSeconds(3));

        before = _tenants.StatementCount;
        var ready = await simulator.AdvanceDueAndPlanAsync(_tenant, Ct);
        Assert.Equal(new GuidedDemoAdvanceResult(0, readyAt + GuidedDemoTiming.StageDuration), ready);
        Assert.Equal(DateTimeKind.Utc, ready.NextCheckAtUtc!.Value.Kind);
        Assert.Equal(1, _tenants.StatementCount - before);
        Assert.Equal(GuidedDemoTiming.StepFor(OrderStatus.ReadyForPickup, readyAt)!.DueAtUtc, ready.NextCheckAtUtc);
    }

    [Fact]
    public async Task EachStageMovesExactlyAtItsDeadline_NotAtTheNextOrderSyncCycle()
    {
        var readyAt = await ReadyPracticeOrderAsync();
        var schedule = new GuidedDemoSchedule();
        var scheduler = Scheduler(schedule);
        schedule.Plan(_tenant, (await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct)).NextCheckAtUtc);

        // A millisecond early: no query at all, nothing moves.
        _time.Set(readyAt + GuidedDemoTiming.StageDuration - TimeSpan.FromMilliseconds(1));
        var before = _tenants.StatementCount;
        Assert.Equal(0, await scheduler.RunDueAsync(Ct));
        Assert.Equal(before, _tenants.StatementCount);
        Assert.Equal(OrderStatus.ReadyForPickup, await StatusAsync());

        // At the deadline: picked up, and the OnTheWay stage is planned a whole stage later.
        _time.Set(readyAt + GuidedDemoTiming.StageDuration);
        Assert.Equal(1, await scheduler.RunDueAsync(Ct));
        Assert.Equal(OrderStatus.OnTheWay, await StatusAsync());
        Assert.Equal(_time.UtcNow + GuidedDemoTiming.StageDuration, schedule.CheckAtUtc(_tenant));

        _time.Advance(GuidedDemoTiming.StageDuration);
        Assert.Equal(1, await scheduler.RunDueAsync(Ct));
        Assert.Equal(OrderStatus.Delivered, await StatusAsync());
        // Delivered needs no Worker step (the Live Screen stops showing it at its own deadline): nothing left to plan.
        Assert.Equal(0, schedule.Count);
        Assert.Null(schedule.NextCheckAtUtc);
    }

    [Fact]
    public async Task TheHostedScheduler_SleepsUntilTheDeadline_WakesThere_AndSleepsAgainForTheNextStage()
    {
        var readyAt = await ReadyPracticeOrderAsync();
        var schedule = new GuidedDemoSchedule();
        var scheduler = Scheduler(schedule);
        await scheduler.StartAsync(Ct);
        try
        {
            schedule.Plan(_tenant, (await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct)).NextCheckAtUtc);
            await Eventually(() => _time.PendingDueTimes.Contains(readyAt + GuidedDemoTiming.StageDuration));

            _time.Advance(GuidedDemoTiming.StageDuration - TimeSpan.FromMilliseconds(1));
            Assert.Equal(OrderStatus.ReadyForPickup, await StatusAsync());

            _time.Advance(TimeSpan.FromMilliseconds(1));
            await Eventually(async () => await StatusAsync() == OrderStatus.OnTheWay);
            var pickedUpAt = _time.UtcNow;
            await Eventually(() => _time.PendingDueTimes.Contains(pickedUpAt + GuidedDemoTiming.StageDuration));

            _time.Advance(GuidedDemoTiming.StageDuration);
            await Eventually(async () => await StatusAsync() == OrderStatus.Delivered);
            await Eventually(() => _time.PendingDueTimes.Count == 0);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithNoOpenPracticeOrder_TheSchedulerHoldsNoTimer_AndRunsNoQuery()
    {
        await _tenants.SeedUserAsync(_tenant, _owner);
        var schedule = new GuidedDemoSchedule();
        var scheduler = Scheduler(schedule);
        var before = _tenants.StatementCount;
        await scheduler.StartAsync(Ct);
        try
        {
            await Task.Delay(150, Ct);
            _time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(50, Ct);
            Assert.Equal(0, _time.TimersCreated);
            Assert.Equal(before, _tenants.StatementCount);

            // The order-sync cycle's read of a tenant without a practice order plans nothing.
            schedule.Plan(_tenant, (await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct)).NextCheckAtUtc);
            Assert.Equal(0, schedule.Count);
            await Task.Delay(50, Ct);
            Assert.Equal(0, _time.TimersCreated);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AfterAWorkerRestart_TheFirstCycleCatchesUpAnOverdueStage_OneStepOnly_ThenTheSchedulerTakesOver()
    {
        await ReadyPracticeOrderAsync();
        // The Worker was down for 90 seconds: its in-memory schedule is gone.
        _time.Advance(TimeSpan.FromSeconds(90));
        var schedule = new GuidedDemoSchedule();
        var scheduler = Scheduler(schedule);
        Assert.Equal(0, await scheduler.RunDueAsync(Ct));

        // The first order-sync cycle reads the tenant: one step (never straight to Delivered) and a fresh deadline.
        var cycle = await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct);
        Assert.Equal(new GuidedDemoAdvanceResult(1, _time.UtcNow + GuidedDemoTiming.StageDuration), cycle);
        Assert.Equal(OrderStatus.OnTheWay, await StatusAsync());
        schedule.Plan(_tenant, cycle.NextCheckAtUtc);
        Assert.Equal(0, (await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct)).Advanced);

        _time.Advance(GuidedDemoTiming.StageDuration);
        Assert.Equal(1, await scheduler.RunDueAsync(Ct));
        Assert.Equal(OrderStatus.Delivered, await StatusAsync());
    }

    [Fact]
    public async Task TwoWorkerInstances_EachWithItsOwnSchedule_MoveTheStageOnce()
    {
        var readyAt = await ReadyPracticeOrderAsync();
        var first = new GuidedDemoSchedule();
        var second = new GuidedDemoSchedule();
        var firstWorker = Scheduler(first);
        var secondWorker = Scheduler(second);
        foreach (var schedule in new[] { first, second })
            schedule.Plan(_tenant, (await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct)).NextCheckAtUtc);

        _time.Set(readyAt + GuidedDemoTiming.StageDuration);
        var moved = await Task.WhenAll(
            Task.Run(() => firstWorker.RunDueAsync(Ct), Ct),
            Task.Run(() => secondWorker.RunDueAsync(Ct), Ct));

        Assert.Equal(1, moved.Sum());
        Assert.Equal(OrderStatus.OnTheWay, await StatusAsync());
        await using var db = await _tenants.CreateAsync(_tenant, Ct);
        var row = db.GuidedDemoSessions.Single();
        Assert.Equal(_time.UtcNow, row.UpdatedAt);
        Assert.Null(row.CompletedAtUtc);
        // Both plan the delivery; whichever wakes first delivers, the other finds nothing to do.
        Assert.Equal(_time.UtcNow + GuidedDemoTiming.StageDuration, first.CheckAtUtc(_tenant));
        Assert.Equal(_time.UtcNow + GuidedDemoTiming.StageDuration, second.CheckAtUtc(_tenant));
        _time.Advance(GuidedDemoTiming.StageDuration);
        Assert.Equal(1, await secondWorker.RunDueAsync(Ct) + await firstWorker.RunDueAsync(Ct));
        Assert.Equal(OrderStatus.Delivered, await StatusAsync());
    }

    [Fact]
    public async Task AStoppedWorker_MovesNothing_AndTheLiveScreenKeepsTheRealPastDeadline()
    {
        var readyAt = await ReadyPracticeOrderAsync();
        _time.Advance(TimeSpan.FromMinutes(2));

        var shown = await Demos().GetForLiveScreenAsync(_tenant, _owner, Ct);
        Assert.Equal(OrderStatus.ReadyForPickup, shown!.Status);
        // The browser counts to zero and waits honestly: the deadline stays the persisted one, now in the past.
        Assert.Equal(new GuidedDemoAutomaticStep(GuidedDemoTiming.PickUp, readyAt + GuidedDemoTiming.StageDuration), shown.Automatic);
        Assert.True(shown.Automatic!.DueAtUtc < _time.UtcNow);
    }

    [Fact]
    public async Task AFailingTenantDatabase_IsLeftToTheNextOrderSyncCycle_NotRetriedInATightLoop()
    {
        var readyAt = await ReadyPracticeOrderAsync();
        var schedule = new GuidedDemoSchedule();
        var scheduler = Scheduler(schedule);
        schedule.Plan(_tenant, readyAt + GuidedDemoTiming.StageDuration);
        _time.Set(readyAt + GuidedDemoTiming.StageDuration);

        _tenants.FailNextStatementContaining = "GuidedDemoSessions";
        Assert.Equal(0, await scheduler.RunDueAsync(Ct));
        Assert.Equal(0, schedule.Count);
        Assert.Equal(OrderStatus.ReadyForPickup, await StatusAsync());

        // The next cycle reads it again: the overdue stage moves then.
        var cycle = await Simulator().AdvanceDueAndPlanAsync(_tenant, Ct);
        Assert.Equal(1, cycle.Advanced);
    }

    [Fact]
    public void TheSchedule_WakesTheSchedulerOnlyWhenItChanges_AndHandsOutDueTenantsOnce()
    {
        var schedule = new GuidedDemoSchedule();
        var at = new DateTime(2026, 10, 3, 9, 0, 20, DateTimeKind.Utc);
        var other = Guid.NewGuid();

        var changed = schedule.Changed;
        schedule.Plan(_tenant, null);
        Assert.False(changed.IsCompleted, "forgetting an unknown tenant changes nothing");
        schedule.Plan(_tenant, at);
        Assert.True(changed.IsCompleted);
        schedule.Plan(other, at.AddSeconds(5));
        Assert.Equal(at, schedule.NextCheckAtUtc);

        Assert.Empty(schedule.TakeDue(at.AddTicks(-1)));
        Assert.Equal([_tenant], schedule.TakeDue(at));
        Assert.Empty(schedule.TakeDue(at));
        Assert.Equal([other], schedule.TakeDue(at.AddMinutes(1)));
        Assert.Equal(0, schedule.Count);
    }

    [Fact]
    public void TheDemoScheduler_LeavesTheOrderSyncCadenceAndProviderWorkAlone()
    {
        var worker = Source("src", "Wasla.Worker", "Jobs", "OrderSyncWorker.cs");
        Assert.Contains("private const int CycleIntervalSeconds = 15;", worker, StringComparison.Ordinal);
        Assert.Contains("private const int CatastrophicFailureBackoffSeconds = 30;", worker, StringComparison.Ordinal);
        Assert.Contains("private const int MaxParallelCustomers = 5;", worker, StringComparison.Ordinal);
        Assert.Contains("var remaining = TimeSpan.FromSeconds(CycleIntervalSeconds) - swCycle.Elapsed;", worker, StringComparison.Ordinal);
        Assert.Contains("var r = await syncer.SyncCustomerWithResultAsync(customer.Id, ct);", worker, StringComparison.Ordinal);
        // The cycle only hands the tenant's next demo deadline to the scheduler.
        Assert.Contains("_demoSchedule.Plan(customer.Id, result.NextCheckAtUtc);", worker, StringComparison.Ordinal);

        var program = Source("src", "Wasla.Worker", "Program.cs");
        Assert.Contains("builder.Services.AddHostedService<OrderSyncWorker>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<GuidedDemoSchedule>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddHostedService<GuidedDemoScheduler>();", program, StringComparison.Ordinal);

        foreach (var file in new[] { "GuidedDemoScheduler.cs", "GuidedDemoSchedule.cs" })
        {
            var source = Source("src", "Wasla.Infrastructure", "Services", file);
            foreach (var forbidden in new[] { "IOrderSyncService", "SyncCustomer", "CycleInterval", "ProviderMode", "IPlatform", "Webhook", "PrintJob", "db.Orders", "Notification", "TenantDbContext" })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        Assert.Contains("GetRequiredService<IGuidedDemoDeliverySimulator>()", Source("src", "Wasla.Infrastructure", "Services", "GuidedDemoScheduler.cs"), StringComparison.Ordinal);
        // Only the Worker hosts it: the Web and API never move a practice order on their own.
        foreach (var host in new[] { new[] { "src", "Wasla.Web", "Program.cs" }, new[] { "src", "Wasla.Api", "Program.cs" }, new[] { "src", "Wasla.Infrastructure", "DependencyInjection", "ServiceCollectionExtensions.cs" } })
            Assert.DoesNotContain("GuidedDemoScheduler", Source(host), StringComparison.Ordinal);
    }

    public void Dispose() => _tenants.Dispose();

    private async Task<DateTime> ReadyPracticeOrderAsync()
    {
        await _tenants.SeedUserAsync(_tenant, _owner);
        var demos = Demos();
        var demo = await demos.StartAsync(_tenant, _owner, Ct);
        foreach (var action in new[] { "approve", "start-preparing", "mark-ready" })
            Assert.True((await demos.ApplyActionAsync(_tenant, _owner, demo.Id, action, Ct)).Succeeded);
        return _time.UtcNow;
    }

    private async Task<OrderStatus> StatusAsync()
    {
        await using var db = await _tenants.CreateAsync(_tenant, CancellationToken.None);
        return db.GuidedDemoSessions.Single().Status;
    }

    private GuidedDemoService Demos() => new(_tenants, new NoSubtypes(), _time);

    private GuidedDemoDeliverySimulator Simulator() => new(_tenants, _time);

    private GuidedDemoScheduler Scheduler(GuidedDemoSchedule schedule)
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantDbContextFactory>(_tenants)
            .AddSingleton<TimeProvider>(_time)
            .AddScoped<IGuidedDemoDeliverySimulator, GuidedDemoDeliverySimulator>()
            .BuildServiceProvider();
        return new GuidedDemoScheduler(
            services.GetRequiredService<IServiceScopeFactory>(),
            schedule,
            _time,
            NullLogger<GuidedDemoScheduler>.Instance);
    }

    private static async Task Eventually(Func<bool> condition) => await Eventually(() => Task.FromResult(condition()));

    /// <summary>The hosted loop runs on the thread pool: waits (briefly, in real time) for it to reach a state.</summary>
    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The scheduler did not reach the expected state.");
            await Task.Delay(10, CancellationToken.None);
        }
    }

    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wasla.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
        return File.ReadAllText(Path.Combine([root, .. parts]));
    }

    private sealed class NoSubtypes : ITenantBusinessSubtypeReader
    {
        public Task<IReadOnlyList<string>?> GetSubtypeCodesAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(null);
    }
}

/// <summary>A clock the test moves, whose timers (as Task.Delay with a TimeProvider uses) fire only when it moves.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    private int _created;

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public int TimersCreated => Volatile.Read(ref _created);

    /// <summary>When each timer still waiting will fire.</summary>
    public IReadOnlyList<DateTime> PendingDueTimes
    {
        get
        {
            lock (_gate)
                return _timers.Where(timer => timer.Due is not null).Select(timer => timer.Due!.Value.UtcDateTime).ToList();
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        Interlocked.Increment(ref _created);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
        }

        return timer;
    }

    public void Set(DateTime utc) => Advance(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) - GetUtcNow());

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
            foreach (var timer in due)
                timer.Due = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private void Change(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_gate)
            timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime;
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Change(this, dueTime);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
