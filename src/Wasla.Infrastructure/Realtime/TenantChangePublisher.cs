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
