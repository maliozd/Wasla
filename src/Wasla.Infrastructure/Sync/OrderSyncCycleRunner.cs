using System.Collections.Concurrent;
using Wasla.Application.Abstractions.Orders.Services;

namespace Wasla.Infrastructure.Sync;

/// <summary>
/// Orders the work of one Worker cycle so that current orders come before history recovery, for every tenant.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 runs the current pass of every active tenant, at most the host's parallelism at a time (the Worker's
/// <c>MaxParallelCustomers</c>). Every eligible tenant gets its current-window check before any tenant starts
/// recovering history.
/// </para>
/// <para>
/// Phase 2 runs backfill turns for the tenants whose history is behind, round-robin: each round gives every pending
/// tenant one turn (one window per connection), for at most
/// <see cref="OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle"/> rounds and only while the
/// <see cref="BackfillBudget"/> lasts. A tenant with a long outage therefore cannot hold the cycle; the remaining
/// history continues next cycle, from a start tenant that rotates each cycle. A tenant whose turn fails stops for
/// this cycle.
/// </para>
/// <para>
/// This orders work inside one Worker process. It is not a distributed scheduler: several Worker instances would
/// process the same tenants and need leases and a distributed request limiter.
/// </para>
/// </remarks>
public sealed class OrderSyncCycleRunner
{
    /// <summary>Time after which phase 2 starts no new backfill turn in this cycle.</summary>
    public static readonly TimeSpan BackfillBudget = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly int _maxParallelTenants;
    private readonly TimeSpan _backfillBudget;
    private int _rotation;

    /// <param name="maxParallelTenants">Tenants synchronized at the same time in each phase.</param>
    public OrderSyncCycleRunner(TimeProvider time, int maxParallelTenants)
        : this(time, maxParallelTenants, BackfillBudget)
    {
    }

    internal OrderSyncCycleRunner(TimeProvider time, int maxParallelTenants, TimeSpan backfillBudget)
    {
        if (maxParallelTenants <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxParallelTenants));

        _time = time;
        _maxParallelTenants = maxParallelTenants;
        _backfillBudget = backfillBudget;
    }

    /// <param name="syncCurrentAsync">The current pass of one tenant; null when it failed outside a connection.</param>
    /// <param name="backfillAsync">One backfill turn of one tenant; null when it failed outside a connection.</param>
    public async Task<OrderSyncCycleResult> RunAsync(
        IReadOnlyList<Guid> tenantIds,
        Func<Guid, CancellationToken, Task<OrderSyncCustomerResult?>> syncCurrentAsync,
        Func<Guid, CancellationToken, Task<OrderSyncCustomerResult?>> backfillAsync,
        CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelTenants, CancellationToken = ct };

        var current = new ConcurrentQueue<OrderSyncCustomerResult>();
        await Parallel.ForEachAsync(tenantIds, options, async (tenantId, innerCt) =>
        {
            if (await syncCurrentAsync(tenantId, innerCt).ConfigureAwait(false) is { } result)
                current.Enqueue(result);
        }).ConfigureAwait(false);

        var behind = current.Where(r => r.BackfillPending).Select(r => r.CustomerId).ToHashSet();
        var pending = Rotate(tenantIds.Where(behind.Contains).ToList());

        var backfill = new ConcurrentQueue<OrderSyncCustomerResult>();
        var deadline = _time.GetUtcNow() + _backfillBudget;
        for (var round = 0; round < OrderFetchWindowPlanner.MaxRecoveryWindowsPerCycle && pending.Count > 0; round++)
        {
            if (_time.GetUtcNow() >= deadline)
                break;

            var stillPending = new ConcurrentDictionary<Guid, bool>();
            await Parallel.ForEachAsync(pending, options, async (tenantId, innerCt) =>
            {
                if (_time.GetUtcNow() >= deadline)
                    return;

                if (await backfillAsync(tenantId, innerCt).ConfigureAwait(false) is not { } result)
                    return;

                backfill.Enqueue(result);
                if (result.BackfillPending)
                    stillPending[tenantId] = true;
            }).ConfigureAwait(false);

            pending = pending.Where(stillPending.ContainsKey).ToList();
        }

        return new OrderSyncCycleResult([.. current], [.. backfill]);
    }

    /// <summary>Starts phase 2 at a different tenant each cycle, so a short budget does not always favour the same ones.</summary>
    private List<Guid> Rotate(List<Guid> tenants)
    {
        if (tenants.Count < 2)
            return tenants;

        var offset = (int)((uint)Interlocked.Increment(ref _rotation) % (uint)tenants.Count);
        return [.. tenants.Skip(offset), .. tenants.Take(offset)];
    }
}

/// <param name="Current">Phase 1 results, one per tenant that returned one.</param>
/// <param name="Backfill">Phase 2 results, one per backfill turn.</param>
public sealed record OrderSyncCycleResult(
    IReadOnlyList<OrderSyncCustomerResult> Current,
    IReadOnlyList<OrderSyncCustomerResult> Backfill);
