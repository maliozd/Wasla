using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class PrintBridgeJobService : IPrintBridgeJobService
{
    private const int MaxPendingLimit = 10;

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ILogger<PrintBridgeJobService> _logger;

    public PrintBridgeJobService(ITenantDbContextFactory dbFactory, ILogger<PrintBridgeJobService> logger)
    {
        _dbFactory = dbFactory;
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
            "Print Bridge pending jobs fetched. CustomerId={CustomerId}, Count={Count}, Max={Max}",
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

        var exists = await db.PrintJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (!exists)
            return new PrintJobClaimResponse(PrintJobClaimResult.NotFound, "PrintBridge.JobNotFound");

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
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogInformation(
                "Print job claimed for printing. CustomerId={CustomerId}, JobId={JobId}, LockedBy={LockedBy}",
                customerId,
                jobId,
                safeLockedBy);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobClaimed");
        }

        _logger.LogInformation(
            "Print job claim skipped because status is no longer Pending. CustomerId={CustomerId}, JobId={JobId}",
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

        var exists = await db.PrintJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (!exists)
            return new PrintJobClaimResponse(PrintJobClaimResult.NotFound, "PrintBridge.JobNotFound");

        var now = DateTime.UtcNow;
        var rows = await db.PrintJobs
            .Where(j => j.Id == jobId && j.Status == PrintJobStatus.Printing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, PrintJobStatus.Printed)
                    .SetProperty(j => j.PrintedAt, now)
                    .SetProperty(j => j.ErrorMessage, (string?)null)
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogInformation(
                "Print job marked printed. CustomerId={CustomerId}, JobId={JobId}",
                customerId,
                jobId);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobPrinted");
        }

        return new PrintJobClaimResponse(PrintJobClaimResult.Skipped, "PrintBridge.JobNotPrinting");
    }

    public async Task<PrintJobClaimResponse> TryMarkFailedAsync(
        Guid customerId,
        Guid jobId,
        string errorMessage,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var exists = await db.PrintJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        if (!exists)
            return new PrintJobClaimResponse(PrintJobClaimResult.NotFound, "PrintBridge.JobNotFound");

        var now = DateTime.UtcNow;
        var safeError = SanitizeErrorMessage(errorMessage);

        var rows = await db.PrintJobs
            .Where(j => j.Id == jobId && j.Status == PrintJobStatus.Printing)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Status, PrintJobStatus.Failed)
                    .SetProperty(j => j.ErrorMessage, safeError)
                    .SetProperty(j => j.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (rows > 0)
        {
            _logger.LogWarning(
                "Print job marked failed. CustomerId={CustomerId}, JobId={JobId}, Error={Error}",
                customerId,
                jobId,
                safeError);
            return new PrintJobClaimResponse(PrintJobClaimResult.Claimed, "PrintBridge.JobFailed");
        }

        return new PrintJobClaimResponse(PrintJobClaimResult.Skipped, "PrintBridge.JobNotPrinting");
    }

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
