using System.Collections.Concurrent;
using Wasla.Application.Abstractions.Orders.Services;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Sync;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Scheduling of one Worker cycle with fake tenants and fake time. No database, no network and no real waiting.
/// </summary>
public sealed class OrderSyncCycleRunnerTests
{
    /// <summary>The Worker's MaxParallelCustomers.</summary>
    private const int WorkerParallelism = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryTenantsCurrentPass_RunsBeforeAnyBackfill()
    {
        var clock = new LimiterClock();
        var events = new ConcurrentQueue<string>();
        var tenants = Tenants(20);
        var behind = tenants.Take(10).ToHashSet();
        var runner = new OrderSyncCycleRunner(clock, WorkerParallelism, OrderSyncCycleRunner.BackfillBudget);

        var cycle = await runner.RunAsync(
            tenants,
            async (id, _) =>
            {
                await Task.Yield();
                events.Enqueue("current");
                return Result(id, pending: behind.Contains(id));
            },
            async (id, _) =>
            {
                await Task.Yield();
                events.Enqueue("backfill");
                return Result(id, pending: true);
            },
            Ct);

        var order = events.ToList();
        Assert.Equal(20, order.Count(e => e == "current"));
        Assert.Equal(20, order.TakeWhile(e => e == "current").Count());
        Assert.Equal(20, cycle.Current.Count);
        Assert.Equal(10 * OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle, cycle.Backfill.Count);
    }

    [Fact]
    public async Task Backfill_IsRoundRobin_OneTurnPerPendingTenantPerRound()
    {
        var clock = new LimiterClock();
        var tenants = Tenants(3);
        var turnsNeeded = new Dictionary<Guid, int> { [tenants[0]] = 1, [tenants[1]] = 4, [tenants[2]] = 2 };
        var turns = new List<Guid>();
        var runner = new OrderSyncCycleRunner(clock, maxParallelTenants: 1, OrderSyncCycleRunner.BackfillBudget);

        await runner.RunAsync(
            tenants,
            (id, _) => Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true)),
            (id, _) =>
            {
                turns.Add(id);
                return Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: --turnsNeeded[id] > 0));
            },
            Ct);

        // The first cycle starts phase 2 at the second tenant (rotation).
        Assert.Equal(
            [tenants[1], tenants[2], tenants[0], tenants[1], tenants[2], tenants[1], tenants[1]],
            turns);
    }

    [Fact]
    public async Task OneTenantWithAHugeBacklog_GetsAtMostTheRecoveryCapPerCycle()
    {
        var clock = new LimiterClock();
        var tenants = Tenants(1);
        var turns = 0;
        var runner = new OrderSyncCycleRunner(clock, WorkerParallelism, OrderSyncCycleRunner.BackfillBudget);

        await runner.RunAsync(
            tenants,
            (id, _) => Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true)),
            (id, _) =>
            {
                turns++;
                return Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true));
            },
            Ct);

        Assert.Equal(OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle, turns);
    }

    [Fact]
    public async Task Backfill_StartsNoTurnAfterTheBudget()
    {
        var clock = new LimiterClock();
        var tenants = Tenants(3);
        var startedAt = new List<DateTimeOffset>();
        var runner = new OrderSyncCycleRunner(clock, maxParallelTenants: 1, TimeSpan.FromSeconds(30));
        var deadline = clock.Now + TimeSpan.FromSeconds(30);

        await runner.RunAsync(
            tenants,
            (id, _) => Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true)),
            (id, _) =>
            {
                startedAt.Add(clock.Now);
                clock.Now += TimeSpan.FromSeconds(4);
                return Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true));
            },
            Ct);

        Assert.Equal(8, startedAt.Count);
        Assert.All(startedAt, at => Assert.True(at < deadline));
    }

    [Fact]
    public async Task FailedTurn_StopsThatTenantForThisCycleOnly()
    {
        var clock = new LimiterClock();
        var tenants = Tenants(2);
        var turns = new List<Guid>();
        var runner = new OrderSyncCycleRunner(clock, maxParallelTenants: 1, OrderSyncCycleRunner.BackfillBudget);

        await runner.RunAsync(
            tenants,
            (id, _) => Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true)),
            (id, _) =>
            {
                turns.Add(id);
                var failed = id == tenants[0];
                return Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: !failed, failed: failed));
            },
            Ct);

        Assert.Equal(1, turns.Count(t => t == tenants[0]));
        Assert.Equal(OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle, turns.Count(t => t == tenants[1]));
    }

    [Fact]
    public async Task BackfillStartTenant_RotatesBetweenCycles()
    {
        var clock = new LimiterClock();
        var tenants = Tenants(3);
        var firstTurns = new List<Guid>();
        var runner = new OrderSyncCycleRunner(clock, maxParallelTenants: 1, OrderSyncCycleRunner.BackfillBudget);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var first = true;
            await runner.RunAsync(
                tenants,
                (id, _) => Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: true)),
                (id, _) =>
                {
                    if (first)
                        firstTurns.Add(id);
                    first = false;
                    return Task.FromResult<OrderSyncCustomerResult?>(Result(id, pending: false));
                },
                Ct);
        }

        Assert.Equal([tenants[1], tenants[2], tenants[0]], firstTurns);
    }

    /// <summary>
    /// Capacity model for 300 tenants sharing the process budget of 40 requests per rolling 10 seconds, one page per
    /// current window. The Worker's fan-out (5 tenants at a time) and the real limiter run on a fake clock.
    /// </summary>
    [Fact]
    public async Task ThreeHundredTenants_OnePageEach_NeedSeventySecondsOfQuota_AndBackfillWaitsForAllOfThem()
    {
        var clock = new LimiterClock();
        var limiter = clock.Limiter();
        var tenants = Tenants(300);
        var behind = tenants.Where((_, i) => i % 30 == 0).ToHashSet();
        var current = new ConcurrentBag<DateTimeOffset>();
        var backfill = new ConcurrentBag<(DateTimeOffset StartedAt, DateTimeOffset GrantedAt)>();
        var start = clock.Now;
        var runner = new OrderSyncCycleRunner(clock, WorkerParallelism, OrderSyncCycleRunner.BackfillBudget);

        await runner.RunAsync(
            tenants,
            async (id, ct) =>
            {
                await limiter.AcquireAsync(ct);
                current.Add(clock.Now);
                return Result(id, pending: behind.Contains(id));
            },
            async (id, ct) =>
            {
                var startedAt = clock.Now;
                await limiter.AcquireAsync(ct);
                backfill.Add((startedAt, clock.Now));
                return Result(id, pending: true);
            },
            Ct);

        Assert.Equal(300, current.Count);
        var lastCurrent = current.Max();
        // 40 at once, then 40 more every 10 seconds: request 300 starts 70 seconds in (at least 75 s at a steady 4/s).
        Assert.Equal(start + TimeSpan.FromSeconds(70), lastCurrent);
        Assert.All(backfill, b => Assert.True(b.GrantedAt >= lastCurrent));

        // Phase 2 starts no turn after its 30-second budget (a turn admitted just before it is granted by then), and
        // the cap of 12 turns per tenant still applies.
        var deadline = lastCurrent + OrderSyncCycleRunner.BackfillBudget;
        Assert.NotEmpty(backfill);
        Assert.All(backfill, b => Assert.True(b.GrantedAt <= deadline));
        Assert.True(backfill.Count <= behind.Count * OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle);
    }

    private static List<Guid> Tenants(int count) =>
        Enumerable.Range(1, count).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();

    private static OrderSyncCustomerResult Result(Guid id, bool pending, bool failed = false) =>
        new(id, 1, 0, 0, 0, 0, 0, failed ? 1 : 0) { BackfillPending = pending };
}
