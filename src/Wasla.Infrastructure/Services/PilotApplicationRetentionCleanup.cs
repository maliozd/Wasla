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
/// failure, so the CLI exits non-zero. A pilot application without a status (which <c>CK_ContactMessages_PilotFields</c>
/// prevents) is reported and recorded the same way and not deleted, so someone looks at the row. No log or result holds
/// what the applicant typed.</para>
/// <para>The batch read uses <c>IX_ContactMessages_Topic_PilotLastActivityAtUtc</c> alone, which covers every column it
/// reads, so it never holds an index key while waiting for a row that another run is deleting (the deadlock of two
/// overlapping runs). If SQL Server still picks the read as a deadlock victim (error 1205), it is read again up to
/// <see cref="MaxReadAttempts"/> times in all, each retry logged. When a read or the removal of old destruction records
/// fails for good, the run stops and returns what it already did, marked <see cref="PilotRetentionRunResult.StopReason"/>,
/// so the deletions made so far are still reported and the CLI exits non-zero.</para>
/// </remarks>
public sealed class PilotApplicationRetentionCleanup : IPilotApplicationRetentionService
{
    /// <summary>The query tag of the batch read, by which a test finds its cached SQL Server plans.</summary>
    public const string ExpiredBatchTag = "WAS-222 pilot retention batch read";

    /// <summary>How many times one batch read is tried in all when SQL Server picks it as a deadlock victim.</summary>
    public const int MaxReadAttempts = 3;

    private const int DeadlockVictim = 1205;

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

        string? stopReason = null;
        var recordsPastRetention = 0;
        try
        {
            await DeleteExpiredAsync(request, now, expired, deleted, noLongerExpired, failed, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            stopReason = Describe(ex);
            _logger.LogError(
                "The pilot application retention cleanup could not read the next batch and stopped; the rows it already handled are reported. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                ex.GetType().Name,
                PilotApplicationDestruction.SqlErrorNumber(ex));
        }

        if (stopReason is null)
        {
            try
            {
                recordsPastRetention = request.DryRun
                    ? await _db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.RetainUntilUtc <= now, ct).ConfigureAwait(false)
                    : await RemoveRecordsPastRetentionAsync(now, request.BatchSize, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                stopReason = Describe(ex);
                _logger.LogError(
                    "The pilot application retention cleanup could not remove destruction records past their retention and stopped; the applications it already handled are reported. ExceptionType={ExceptionType} SqlErrorNumber={SqlErrorNumber}",
                    ex.GetType().Name,
                    PilotApplicationDestruction.SqlErrorNumber(ex));
            }
        }

        var result = new PilotRetentionRunResult
        {
            NowUtc = now,
            DryRun = request.DryRun,
            Expired = expired,
            Deleted = deleted,
            NoLongerExpired = noLongerExpired,
            Failed = failed,
            DestructionRecordsPastRetention = recordsPastRetention,
            StopReason = stopReason
        };

        _logger.Log(
            result.Succeeded ? LogLevel.Information : LogLevel.Error,
            "Pilot application retention cleanup finished. DryRun={DryRun} Expired={Expired} Deleted={Deleted} NoLongerExpired={NoLongerExpired} Failed={Failed} DestructionRecordsPastRetention={DestructionRecordsPastRetention} StoppedEarly={StoppedEarly}",
            result.DryRun,
            expired.Count,
            deleted.Count,
            noLongerExpired.Count,
            failed.Count,
            recordsPastRetention,
            result.StoppedEarly);

        return result;
    }

    // Reads expired applications batch by batch and deletes each, filling the lists as it goes, so a read that fails
    // later leaves the caller with everything done before it.
    private async Task DeleteExpiredAsync(
        PilotRetentionRunRequest request,
        DateTime now,
        List<PilotRetentionCandidate> expired,
        List<Guid> deleted,
        List<Guid> noLongerExpired,
        List<Guid> failed,
        CancellationToken ct)
    {
        DateTime? lastActivity = null;
        var seenAtLastActivity = new List<Guid>();
        while (true)
        {
            var batch = await ReadExpiredWithRetryAsync(now, lastActivity, seenAtLastActivity, request.BatchSize, ct).ConfigureAwait(false);
            foreach (var candidate in batch)
            {
                expired.Add(candidate);
                if (candidate.LastActivityAtUtc != lastActivity)
                {
                    lastActivity = candidate.LastActivityAtUtc;
                    seenAtLastActivity.Clear();
                }

                seenAtLastActivity.Add(candidate.Id);
                if (candidate.Status is null)
                {
                    failed.Add(candidate.Id);
                    _logger.LogError(
                        "Pilot application {ContactMessageId} has a last activity but no pilot status; the retention cleanup did not delete it. Correct the row; the next run deletes it if it is still expired.",
                        candidate.Id);
                    if (!request.DryRun)
                        await RecordFailureAsync(candidate.Id, now, ct).ConfigureAwait(false);
                    continue;
                }

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
    }

    private async Task<List<PilotRetentionCandidate>> ReadExpiredWithRetryAsync(
        DateTime now,
        DateTime? after,
        IReadOnlyCollection<Guid> seenAtAfter,
        int batchSize,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadExpiredAsync(now, after, seenAtAfter, batchSize, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxReadAttempts
                && !ct.IsCancellationRequested
                && PilotApplicationDestruction.SqlErrorNumber(ex) == DeadlockVictim)
            {
                _logger.LogWarning(
                    "The pilot application retention batch read lost a deadlock (SQL Server error {SqlErrorNumber}), attempt {Attempt} of {MaxAttempts}; reading the batch again.",
                    DeadlockVictim,
                    attempt,
                    MaxReadAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<PilotRetentionCandidate>> ReadExpiredAsync(
        DateTime now,
        DateTime? after,
        IReadOnlyCollection<Guid> seenAtAfter,
        int batchSize,
        CancellationToken ct)
    {
        var query = _db.ContactMessages.AsNoTracking().TagWith(ExpiredBatchTag).Where(PilotApplicationRetention.ExpiredAt(now));
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
                r.PilotStatus,
                r.PilotLastActivityAtUtc!.Value,
                PilotApplicationRetention.DeleteOnOrAfter(r.PilotLastActivityAtUtc!.Value)))
            .ToList();
    }

    private static string Describe(Exception ex) =>
        PilotApplicationDestruction.SqlErrorNumber(ex) is { } number
            ? $"{ex.GetType().Name} (SQL Server error {number})"
            : ex.GetType().Name;

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
