using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Contact;
using Wasla.Application.Contact;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The pilot application retention cleanup (WAS-222). It reads expired applications in rounds of at most the batch size,
/// oldest activity first, and deletes each in its own short transaction with a destruction record
/// (<see cref="PilotApplicationDestruction"/>). Then it removes destruction records whose own three-year retention has
/// ended. A dry run reads the same rows and changes nothing.
/// </summary>
/// <remarks>
/// <para>One instant (<c>now</c>) is taken at the start and used for the whole run, so the selection does not drift while
/// it runs. Each round continues after the last row of the previous one, so a row that could not be deleted is not read
/// again in the same run and the run always ends.</para>
/// <para>A failed deletion is logged with the row id, the exception type and the SQL Server error number only, recorded
/// as a failed destruction (best effort), and left for the next run; the run continues with the other rows and reports
/// failure, so the CLI exits non-zero. No log or result holds what the applicant typed.</para>
/// </remarks>
public sealed class PilotApplicationRetentionCleanup : IPilotApplicationRetentionService
{
    private readonly CentralDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<PilotApplicationRetentionCleanup> _logger;

    public PilotApplicationRetentionCleanup(CentralDbContext db, TimeProvider time, ILogger<PilotApplicationRetentionCleanup> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    public async Task<PilotRetentionRunResult> RunAsync(PilotRetentionRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize is < 1 or > PilotRetentionRun.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(request), $"The batch size is from 1 to {PilotRetentionRun.MaxBatchSize}.");

        var now = _time.GetUtcNow().UtcDateTime;
        var expired = new List<PilotRetentionCandidate>();
        var deleted = new List<Guid>();
        var noLongerExpired = new List<Guid>();
        var failed = new List<Guid>();

        DateTime? lastActivity = null;
        var seenAtLastActivity = new List<Guid>();
        while (true)
        {
            var batch = await ReadExpiredAsync(now, lastActivity, seenAtLastActivity, request.BatchSize, ct).ConfigureAwait(false);
            foreach (var candidate in batch)
            {
                expired.Add(candidate);
                if (candidate.LastActivityAtUtc != lastActivity)
                {
                    lastActivity = candidate.LastActivityAtUtc;
                    seenAtLastActivity.Clear();
                }

                seenAtLastActivity.Add(candidate.Id);
                if (request.DryRun)
                    continue;

                try
                {
                    var removed = await PilotApplicationDestruction.TryDeleteAsync(
                        _db,
                        candidate.Id,
                        PilotApplicationRetention.ExpiredAt(now),
                        DataDestructionMethod.RetentionExpired,
                        DataDestructionActor.RetentionJob,
                        actorUserId: null,
                        now,
                        ct).ConfigureAwait(false);
                    (removed ? deleted : noLongerExpired).Add(candidate.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(candidate.Id);
                    _logger.LogError(
                        "Pilot application {ContactMessageId} could not be deleted by the retention cleanup; it stays stored for the next run. ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType} SqlErrorNumber={SqlErrorNumber}",
                        candidate.Id,
                        ex.GetType().Name,
                        ex.InnerException?.GetType().Name,
                        PilotApplicationDestruction.SqlErrorNumber(ex));
                    await RecordFailureAsync(candidate.Id, now, ct).ConfigureAwait(false);
                }
            }

            if (batch.Count < request.BatchSize)
                break;
        }

        var recordsPastRetention = request.DryRun
            ? await _db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.RetainUntilUtc <= now, ct).ConfigureAwait(false)
            : await RemoveRecordsPastRetentionAsync(now, request.BatchSize, ct).ConfigureAwait(false);

        var result = new PilotRetentionRunResult
        {
            NowUtc = now,
            DryRun = request.DryRun,
            Expired = expired,
            Deleted = deleted,
            NoLongerExpired = noLongerExpired,
            Failed = failed,
            DestructionRecordsPastRetention = recordsPastRetention
        };

        _logger.Log(
            result.Succeeded ? LogLevel.Information : LogLevel.Error,
            "Pilot application retention cleanup finished. DryRun={DryRun} Expired={Expired} Deleted={Deleted} NoLongerExpired={NoLongerExpired} Failed={Failed} DestructionRecordsPastRetention={DestructionRecordsPastRetention}",
            result.DryRun,
            expired.Count,
            deleted.Count,
            noLongerExpired.Count,
            failed.Count,
            recordsPastRetention);

        return result;
    }

    private async Task<List<PilotRetentionCandidate>> ReadExpiredAsync(
        DateTime now,
        DateTime? after,
        IReadOnlyCollection<Guid> seenAtAfter,
        int batchSize,
        CancellationToken ct)
    {
        var query = _db.ContactMessages.AsNoTracking().Where(PilotApplicationRetention.ExpiredAt(now));
        if (after is { } last)
        {
            var seen = seenAtAfter.ToList();
            query = query.Where(m => m.PilotLastActivityAtUtc > last || (m.PilotLastActivityAtUtc == last && !seen.Contains(m.Id)));
        }

        var rows = await query
            .OrderBy(m => m.PilotLastActivityAtUtc)
            .ThenBy(m => m.Id)
            .Take(batchSize)
            .Select(m => new { m.Id, m.PilotStatus, m.PilotLastActivityAtUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new PilotRetentionCandidate(
                r.Id,
                r.PilotStatus!.Value,
                r.PilotLastActivityAtUtc!.Value,
                PilotApplicationRetention.DeleteOnOrAfter(r.PilotLastActivityAtUtc!.Value)))
            .ToList();
    }

    private async Task RecordFailureAsync(Guid id, DateTime now, CancellationToken ct)
    {
        try
        {
            await PilotApplicationDestruction.RecordFailureAsync(
                _db, id, DataDestructionMethod.RetentionExpired, DataDestructionActor.RetentionJob, actorUserId: null, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "The failed deletion of pilot application {ContactMessageId} could not be recorded either. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                id,
                ex.GetType().Name,
                PilotApplicationDestruction.SqlErrorNumber(ex));
        }
    }

    private async Task<int> RemoveRecordsPastRetentionAsync(DateTime now, int batchSize, CancellationToken ct)
    {
        var removed = 0;
        while (true)
        {
            var ids = await _db.DataDestructionRecords.AsNoTracking()
                .Where(r => r.RetainUntilUtc <= now)
                .OrderBy(r => r.RetainUntilUtc)
                .ThenBy(r => r.Id)
                .Select(r => r.Id)
                .Take(batchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (ids.Count == 0)
                break;

            removed += await _db.DataDestructionRecords
                .Where(r => ids.Contains(r.Id) && r.RetainUntilUtc <= now)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            if (ids.Count < batchSize)
                break;
        }

        return removed;
    }
}
