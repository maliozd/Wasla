using Microsoft.EntityFrameworkCore;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Called for a tenant by Wasla.Worker: once per order-sync cycle, and by the demo scheduler
/// (<see cref="GuidedDemoScheduler"/>) when a practice order's stage is due. Updates only GuidedDemoSessions. How long
/// a demo stays in each courier state is <see cref="GuidedDemoTiming.StageDuration"/>, measured from the session's
/// UpdatedAt (which every transition sets): the same rule the Live Screen's countdown shows, so no extra column is needed.
/// </summary>
public sealed class GuidedDemoDeliverySimulator : IGuidedDemoDeliverySimulator
{
    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;

    public GuidedDemoDeliverySimulator(ITenantDbContextFactory tenantDbs, TimeProvider time)
    {
        _tenantDbs = tenantDbs;
        _time = time;
    }

    public async Task<int> AdvanceDueAsync(Guid tenantId, CancellationToken ct) =>
        (await AdvanceDueAndPlanAsync(tenantId, ct).ConfigureAwait(false)).Advanced;

    public async Task<GuidedDemoAdvanceResult> AdvanceDueAndPlanAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);

        // One small read: the tenant's open, unexpired practice orders (at most one per user, through the filtered
        // open-session index). A tenant without one costs only this statement.
        var open = await db.GuidedDemoSessions
            .AsNoTracking()
            .Where(session => session.CompletedAtUtc == null
                && session.ExpiresAtUtc > now
                && (session.Status == OrderStatus.New
                    || session.Status == OrderStatus.Accepted
                    || session.Status == OrderStatus.Preparing
                    || session.Status == OrderStatus.ReadyForPickup
                    || session.Status == OrderStatus.OnTheWay))
            .Select(session => new OpenStage(session.Status, session.UpdatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (open.Count == 0)
            return new GuidedDemoAdvanceResult(0, null);

        var dueFrom = GuidedDemoTiming.DueFrom(now);
        var advanced = 0;

        // Delivery runs first: a demo picked up in this call has UpdatedAt = now, so it waits at
        // least one more stage before it can be delivered.
        if (open.Any(stage => stage.Status == OrderStatus.OnTheWay && stage.UpdatedAt <= dueFrom))
            advanced += await AdvanceAsync(db, OrderStatus.OnTheWay, OrderStatus.Delivered, now, ct).ConfigureAwait(false);

        var pickUpDue = open.Any(stage => stage.Status == OrderStatus.ReadyForPickup && stage.UpdatedAt <= dueFrom);
        if (pickUpDue)
            advanced += await AdvanceAsync(db, OrderStatus.ReadyForPickup, OrderStatus.OnTheWay, now, ct).ConfigureAwait(false);

        return new GuidedDemoAdvanceResult(advanced, NextCheckAt(open, pickUpDue, now, dueFrom));
    }

    /// <summary>
    /// The earliest moment one of these practice orders needs the Worker again: an automatic stage's deadline (the
    /// OnTheWay stage a pick-up just started ends a whole stage from now), or soon while a user step is pending.
    /// Always after <paramref name="now"/>, so the scheduler never spins.
    /// </summary>
    private static DateTime? NextCheckAt(IReadOnlyList<OpenStage> open, bool pickedUp, DateTime now, DateTime dueFrom)
    {
        DateTime? next = pickedUp ? GuidedDemoTiming.DueAt(now) : null;
        foreach (var stage in open)
        {
            DateTime? at = stage.Status switch
            {
                OrderStatus.ReadyForPickup or OrderStatus.OnTheWay when stage.UpdatedAt > dueFrom =>
                    GuidedDemoTiming.DueAt(stage.UpdatedAt),
                OrderStatus.New or OrderStatus.Accepted or OrderStatus.Preparing =>
                    now + GuidedDemoTiming.UserStepCheckInterval,
                _ => null
            };
            if (at is { } value && (next is null || value < next))
                next = value;
        }

        return next is { } result ? DateTime.SpecifyKind(result, DateTimeKind.Utc) : null;
    }

    /// <summary>
    /// One conditional statement: only an open, unexpired session in exactly <paramref name="from"/>
    /// can move, so a repeated cycle or a second Worker instance finds nothing left to update.
    /// </summary>
    private static Task<int> AdvanceAsync(
        TenantDbContext db,
        OrderStatus from,
        OrderStatus to,
        DateTime now,
        CancellationToken ct)
    {
        var enteredBefore = GuidedDemoTiming.DueFrom(now);
        DateTime? completedAt = to == OrderStatus.Delivered ? now : null;
        return db.GuidedDemoSessions
            .Where(session => session.Status == from
                && session.CompletedAtUtc == null
                && session.ExpiresAtUtc > now
                && session.UpdatedAt <= enteredBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, to)
                .SetProperty(session => session.CompletedAtUtc, completedAt)
                .SetProperty(session => session.UpdatedAt, now), ct);
    }

    private sealed record OpenStage(OrderStatus Status, DateTime UpdatedAt);
}
