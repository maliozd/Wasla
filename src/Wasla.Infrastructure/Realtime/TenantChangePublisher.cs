using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Realtime;

/// <summary>
/// Publishes a change notification after the change has been committed. A notification is never part of the work it
/// reports: whatever the notifier does (throws, times out, is cancelled), the caller's result stays as it is, and only
/// the exception type is logged.
/// </summary>
public static class TenantChangePublisher
{
    public static async Task PublishAsync(
        ITenantChangeNotifier notifier,
        Guid tenantId,
        TenantChange change,
        ILogger logger)
    {
        try
        {
            // Not the request's token: the work is already committed, so an aborted request must not drop its notice.
            await notifier.NotifyAsync(tenantId, change, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Tenant change notification was not delivered ({ExceptionType}). TenantId={TenantId}, Change={Change}",
                ex.GetType().Name,
                tenantId,
                change.GetType().Name);
        }
    }
}

/// <summary>The default notifier of a process that has no live channel: it drops every notification.</summary>
public sealed class NullTenantChangeNotifier : ITenantChangeNotifier
{
    public static NullTenantChangeNotifier Instance { get; } = new();

    public Task NotifyAsync(Guid tenantId, TenantChange change, CancellationToken ct) => Task.CompletedTask;
}

/// <summary><c>Order.ChangeVersion</c>, the per-order change sequence of Live Screen invalidation (WAS-24).</summary>
public static class OrderChangeVersions
{
    /// <summary>The version of an order when it is first stored.</summary>
    public const long Initial = 1;

    /// <summary>
    /// Adds one to the order's <c>ChangeVersion</c> with an atomic <c>UPDATE ... SET ChangeVersion = ChangeVersion + 1</c>
    /// and returns the new value. Call it inside the transaction that writes the change, after the change: the row
    /// stays locked until the commit, so the value read back is this change's own, and a snapshot can never show the
    /// version without the change.
    /// </summary>
    public static async Task<long> NextAsync(TenantDbContext db, Guid orderId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An order's change version is raised only inside the transaction of the change.");

        await db.Orders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ChangeVersion, o => o.ChangeVersion + 1), ct)
            .ConfigureAwait(false);
        return await db.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => o.ChangeVersion)
            .SingleAsync(ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Order-scoped <c>PrintJob.StatusVersion</c> values.</summary>
public static class PrintJobStatusVersions
{
    /// <summary>
    /// The version for a new job of <paramref name="orderId"/>: one above every job of that order. Call it inside the
    /// serializable transaction that checks for an active job, so no other job of the order can be created or move
    /// between this read and the insert.
    /// </summary>
    public static async Task<long> NextForNewJobAsync(TenantDbContext db, Guid orderId, CancellationToken ct)
    {
        var highest = await db.PrintJobs
            .Where(j => j.OrderId == orderId)
            .MaxAsync(j => (long?)j.StatusVersion, ct)
            .ConfigureAwait(false);
        return (highest ?? 0) + 1;
    }
}
