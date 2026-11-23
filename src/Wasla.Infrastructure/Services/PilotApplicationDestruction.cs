using System.Data;
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The one way a pilot application is destroyed (WAS-222), shared by the retention cleanup and a Central Admin deletion
/// on request: in one transaction, a conditional delete of the row and, only when that delete removed it, a destruction
/// record. The condition is evaluated by the database inside the delete, so a row that changed since it was read (new
/// activity, a running notification attempt, another run that deleted it first) is not deleted, and two callers at once
/// delete and record it once: on SQL Server the second delete waits for the first one's row lock and then finds nothing.
/// </summary>
internal static class PilotApplicationDestruction
{
    public static async Task<bool> TryDeleteAsync(
        CentralDbContext db,
        Guid id,
        Expression<Func<ContactMessage, bool>> condition,
        DataDestructionMethod method,
        DataDestructionActor actor,
        Guid? actorUserId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var record = DataDestructionRecord.Create(
            DataDestructionSubject.PilotApplication, id, method, DataDestructionResult.Deleted, actor, actorUserId, nowUtc);

        try
        {
            // Read committed, stated explicitly: a pooled connection may carry an earlier stricter level.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var deleted = await db.ContactMessages
                .Where(m => m.Id == id)
                .Where(condition)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            if (deleted == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            db.DataDestructionRecords.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // A failed attempt must not leave its record queued for the next save.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Records a failed destruction attempt. Best effort: the caller has already logged the failure.</summary>
    public static async Task RecordFailureAsync(
        CentralDbContext db,
        Guid id,
        DataDestructionMethod method,
        DataDestructionActor actor,
        Guid? actorUserId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        try
        {
            db.DataDestructionRecords.Add(DataDestructionRecord.Create(
                DataDestructionSubject.PilotApplication, id, method, DataDestructionResult.Failed, actor, actorUserId, nowUtc));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public static int? SqlErrorNumber(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql.Number;
        }

        return null;
    }
}
