using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Infrastructure.Services;

public sealed class PrintBridgeJobService : IPrintBridgeJobService
{
    private const int MaxPendingLimit = 10;
    private const string JobNotFoundKey = "PrintBridge.JobNotFound";
    private const string JobNotPrintingKey = "PrintBridge.JobNotPrinting";

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ITenantChangeNotifier _notifier;
    private readonly ILogger<PrintBridgeJobService> _logger;

    public PrintBridgeJobService(
        ITenantDbContextFactory dbFactory,
        ITenantChangeNotifier notifier,
        ILogger<PrintBridgeJobService> logger)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PendingPrintJobDto>> GetPendingJobsAsync(
        Guid customerId,
        int max,
        CancellationToken ct)
    {
        var safeMax = Math.Clamp(max, 1, MaxPendingLimit);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var jobs = await db.PrintJobs
            .AsNoTracking()
            .Where(j => j.Status == PrintJobStatus.Pending && j.Type == PrintJobType.Receipt)
            .OrderBy(j => j.CreatedAt)
            .Take(safeMax)
            .Select(j => new PendingPrintJobDto(
                j.Id,
                j.OrderId,
                j.Type.ToString(),
                j.CopyCount,
                j.PayloadJson,
                j.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Print Bridge pending jobs fetched. TenantId={TenantId}, Count={Count}, Max={Max}",
            customerId,
            jobs.Count,
            safeMax);

        return jobs;
    }

    public async Task<PrintJobClaimResponse> TryMarkPrintingAsync(
        Guid customerId,
        Guid jobId,
        string lockedBy,
        Guid? lockedByInstallationId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        if (!await JobExistsAsync(db, jobId, ct).ConfigureAwait(false))
            return JobNotFound();

        var now = DateTime.UtcNow;
        var safeLockedBy = SanitizeLockedBy(lockedBy);

        var rows = await db.PrintJobs
            .Where(j => j.Id == jobId && j.Status == PrintJobStatus.Pending)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, PrintJobStatus.Printing)
                    .SetProperty(j => j.LockedAt, now)
                    .SetProperty(j => j.LockedBy, safeLockedBy)
                    .SetProperty(j => j.LockedByInstallationId, lockedByInstallationId)
                    .SetProperty(j => j.LastAttemptAt, now)
                    .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1)
                    .SetProperty(j => j.StatusVersion, j => j.StatusVersion + 1)
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogInformation(
                "Print job claimed for printing. TenantId={TenantId}, JobId={JobId}, LockedBy={LockedBy}",
                customerId,
                jobId,
                safeLockedBy);
            await PublishCurrentStateAsync(db, customerId, jobId).ConfigureAwait(false);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobClaimed");
        }

        _logger.LogInformation(
            "Print job claim skipped because status is no longer Pending. TenantId={TenantId}, JobId={JobId}",
            customerId,
            jobId);
        return new PrintJobClaimResponse(PrintJobClaimResult.Skipped, "PrintBridge.JobNotPending");
    }

    public async Task<PrintJobClaimResponse> TryMarkPrintedAsync(
        Guid customerId,
        Guid jobId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        if (!await JobExistsAsync(db, jobId, ct).ConfigureAwait(false))
            return JobNotFound();

        var now = DateTime.UtcNow;
        var rows = await db.PrintJobs
            .Where(j => j.Id == jobId && j.Status == PrintJobStatus.Printing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, PrintJobStatus.Printed)
                    .SetProperty(j => j.PrintedAt, now)
                    .SetProperty(j => j.ErrorMessage, (string?)null)
                    .SetProperty(j => j.StatusVersion, j => j.StatusVersion + 1)
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogInformation(
                "Print job marked printed. TenantId={TenantId}, JobId={JobId}",
                customerId,
                jobId);
            await PublishCurrentStateAsync(db, customerId, jobId).ConfigureAwait(false);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobPrinted");
        }

        return JobNotPrinting();
    }

    public async Task<PrintJobClaimResponse> TryMarkFailedAsync(
        Guid customerId,
        Guid jobId,
        string errorMessage,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        if (!await JobExistsAsync(db, jobId, ct).ConfigureAwait(false))
            return JobNotFound();

        var now = DateTime.UtcNow;
        var safeError = SanitizeErrorMessage(errorMessage);

        var rows = await db.PrintJobs
            .Where(j => j.Id == jobId && j.Status == PrintJobStatus.Printing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, PrintJobStatus.Failed)
                    .SetProperty(j => j.ErrorMessage, safeError)
                    .SetProperty(j => j.StatusVersion, j => j.StatusVersion + 1)
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogWarning(
                "Print job marked failed. TenantId={TenantId}, JobId={JobId}, Error={Error}",
                customerId,
                jobId,
                safeError);
            await PublishCurrentStateAsync(db, customerId, jobId).ConfigureAwait(false);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobFailed");
        }

        return JobNotPrinting();
    }

    private static async Task<bool> JobExistsAsync(TenantDbContext db, Guid jobId, CancellationToken ct) =>
        await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(j => j.Id == jobId, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// After a conditional update succeeded (each runs as its own committed statement), reads the job's status and
    /// version together and publishes them. A later transition may already have happened; the pair read is still
    /// consistent, and the version tells screens which notification is newest. Nothing here can change the response
    /// the Bridge gets for its report.
    /// </summary>
    private async Task PublishCurrentStateAsync(TenantDbContext db, Guid customerId, Guid jobId)
    {
        PrintJobChanged? change;
        try
        {
            // Bounded, so a slow read cannot hold the Bridge's response; not the request's token (the work is committed).
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            change = await db.PrintJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => new PrintJobChanged(j.OrderId, j.Id, j.Status, j.StatusVersion))
                .FirstOrDefaultAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Print job state could not be read for its change notification ({ExceptionType}). TenantId={TenantId}, JobId={JobId}",
                ex.GetType().Name,
                customerId,
                jobId);
            return;
        }

        if (change is not null)
            await TenantChangePublisher.PublishAsync(_notifier, customerId, change, _logger).ConfigureAwait(false);
    }

    private static PrintJobClaimResponse JobNotFound() =>
        new(PrintJobClaimResult.NotFound, JobNotFoundKey);

    private static PrintJobClaimResponse JobNotPrinting() =>
        new(PrintJobClaimResult.Skipped, JobNotPrintingKey);

    private static string SanitizeLockedBy(string lockedBy)
    {
        var s = (lockedBy ?? string.Empty).Trim();
        if (s.Length == 0) return "print-bridge";
        return s.Length <= 200 ? s : s[..200];
    }

    private static string SanitizeErrorMessage(string errorMessage)
    {
        var msg = (errorMessage ?? "Unknown error").Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length <= 1000 ? msg : msg[..1000];
    }
}
