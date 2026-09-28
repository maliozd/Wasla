using Microsoft.EntityFrameworkCore;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Called once per tenant in each Wasla.Worker cycle. Updates only GuidedDemoSessions.
/// </summary>
public sealed class GuidedDemoDeliverySimulator : IGuidedDemoDeliverySimulator
{
    /// <summary>
    /// How long a demo stays in each courier state before the next one. Measured from the session's
    /// UpdatedAt, which every transition sets, so no extra column is needed.
    /// </summary>
    public static readonly TimeSpan CourierDelay = TimeSpan.FromSeconds(8);

    private readonly ITenantDbContextFactory _tenantDbs;
    private readonly TimeProvider _time;

    public GuidedDemoDeliverySimulator(ITenantDbContextFactory tenantDbs, TimeProvider time)
    {
        _tenantDbs = tenantDbs;
        _time = time;
    }

    public async Task<int> AdvanceDueAsync(Guid tenantId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _tenantDbs.CreateAsync(tenantId, ct).ConfigureAwait(false);

        // Delivery runs first: a demo picked up in this cycle has UpdatedAt = now, so it waits at
        // least one more CourierDelay before it can be delivered.
        var delivered = await AdvanceAsync(db, OrderStatus.OnTheWay, OrderStatus.Delivered, now, ct).ConfigureAwait(false);
        var pickedUp = await AdvanceAsync(db, OrderStatus.ReadyForPickup, OrderStatus.OnTheWay, now, ct).ConfigureAwait(false);
        return delivered + pickedUp;
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
        var enteredBefore = now - CourierDelay;
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
}
