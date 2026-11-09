using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// Manual receipt printing. Creation goes through <see cref="IReceiptPrintJobService" /> and
/// re-printing through <see cref="IPrintJobHistoryService" />, so the receipt payload, copy-count
/// clamping, and active-job guards all stay in the existing pipeline.
/// </summary>
public sealed class ManualOrderPrintService : IManualOrderPrintService
{
    private static readonly Guid TenantOperationalSettingsSingletonId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IReceiptPrintJobService _receiptJobs;
    private readonly IPrintJobHistoryService _printJobHistory;
    private readonly ILogger<ManualOrderPrintService> _logger;

    public ManualOrderPrintService(
        ITenantDbContextFactory dbFactory,
        IReceiptPrintJobService receiptJobs,
        IPrintJobHistoryService printJobHistory,
        ILogger<ManualOrderPrintService> logger)
    {
        _dbFactory = dbFactory;
        _receiptJobs = receiptJobs;
        _printJobHistory = printJobHistory;
        _logger = logger;
    }

    public async Task<OrderReceiptPrintState> GetReceiptPrintStateAsync(
        Guid tenantId,
        Guid orderId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
        return BuildState(await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false));
    }

    public async Task<OrderReceiptPrintState?> FindReceiptPrintStateAsync(
        Guid tenantId,
        Guid orderId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        // Tenant isolation: the order is looked up inside this tenant's database only.
        var orderExists = await db.Orders
            .AsNoTracking()
            .AnyAsync(o => o.Id == orderId, ct)
            .ConfigureAwait(false);

        return orderExists
            ? BuildState(await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false))
            : null;
    }

    public Task<ManualOrderPrintResult> QueueReceiptPrintAsync(
        Guid tenantId,
        Guid orderId,
        string? tenantDisplayName,
        CancellationToken ct) =>
        QueueAsync(tenantId, orderId, tenantDisplayName, reprintConfirmed: false, ct);

    public Task<ManualOrderPrintResult> QueueConfirmedReprintAsync(
        Guid tenantId,
        Guid orderId,
        string? tenantDisplayName,
        CancellationToken ct) =>
        QueueAsync(tenantId, orderId, tenantDisplayName, reprintConfirmed: true, ct);

    private async Task<ManualOrderPrintResult> QueueAsync(
        Guid tenantId,
        Guid orderId,
        string? tenantDisplayName,
        bool reprintConfirmed,
        CancellationToken ct)
    {
        int copyCount;
        ReceiptJobRow? reprintSource;

        await using (var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false))
        {
            // Tenant isolation: the order is looked up inside this tenant's database only.
            var orderExists = await db.Orders
                .AsNoTracking()
                .AnyAsync(o => o.Id == orderId, ct)
                .ConfigureAwait(false);

            if (!orderExists)
            {
                return new ManualOrderPrintResult(
                    false,
                    ManualOrderPrintOutcome.OrderNotFound,
                    ManualOrderPrintMessageKeys.OrderNotFound,
                    null);
            }

            var jobs = await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false);

            if (jobs.Any(IsActive))
                return AlreadyQueued();

            reprintSource = jobs.FirstOrDefault(j => j.Status is PrintJobStatus.Printed or PrintJobStatus.Failed);

            // The screen still offered a first print, but a receipt has finished since. Another physical copy needs the
            // operator's confirmation, so nothing is created and the screen reads the current state.
            if (reprintSource is not null && !reprintConfirmed)
                return ReprintConfirmationRequired();

            var settings = await db.TenantOperationalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == TenantOperationalSettingsSingletonId, ct)
                .ConfigureAwait(false);

            // Manual printing is an explicit operator action: the automatic-print toggle does not gate it,
            // but the configured copy count still applies.
            copyCount = settings?.ReceiptPrintCopyCount ?? 1;
        }

        if (reprintSource is not null)
        {
            var reprint = await _printJobHistory
                .CreateReprintAsync(tenantId, reprintSource.Id, tenantDisplayName, ct)
                .ConfigureAwait(false);

            if (reprint.Success)
            {
                return new ManualOrderPrintResult(
                    true,
                    ManualOrderPrintOutcome.Requeued,
                    ManualOrderPrintMessageKeys.ReprintQueued,
                    reprint.NewPrintJobId);
            }

            // A concurrent request won the race and already queued a job for this order.
            if (string.Equals(reprint.MessageKey, "PrintBridge.ReprintAlreadyPending", StringComparison.Ordinal)
                || string.Equals(reprint.MessageKey, "PrintBridge.ReprintNotAllowed", StringComparison.Ordinal))
            {
                return AlreadyQueued();
            }

            _logger.LogWarning(
                "Manual receipt reprint failed. TenantId={TenantId}, OrderId={OrderId}, SourceJobId={SourceJobId}, MessageKey={MessageKey}",
                tenantId,
                orderId,
                reprintSource.Id,
                reprint.MessageKey);

            return new ManualOrderPrintResult(
                false,
                ManualOrderPrintOutcome.Failed,
                reprint.MessageKey,
                null);
        }

        // Only a print the operator confirmed may follow a receipt that finished since the read above.
        var created = await (reprintConfirmed
                ? _receiptJobs.TryCreateReceiptJobAsync(tenantId, orderId, copyCount, tenantDisplayName, ct)
                : _receiptJobs.TryCreateFirstReceiptJobAsync(tenantId, orderId, copyCount, tenantDisplayName, ct))
            .ConfigureAwait(false);

        if (!created)
        {
            // Creation declines when an active job already exists (including the serializable-transaction duplicate
            // race) or, for a first print, when a receipt has finished, so re-read to report the real state.
            await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            var jobs = await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false);

            if (jobs.Any(IsActive))
                return AlreadyQueued();

            if (!reprintConfirmed && jobs.Any(j => j.Status is PrintJobStatus.Printed or PrintJobStatus.Failed))
                return ReprintConfirmationRequired();

            _logger.LogWarning(
                "Manual receipt print could not be queued. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);

            return new ManualOrderPrintResult(
                false,
                ManualOrderPrintOutcome.Failed,
                ManualOrderPrintMessageKeys.Failed,
                null);
        }

        _logger.LogInformation(
            "Manual receipt print queued. TenantId={TenantId}, OrderId={OrderId}",
            tenantId,
            orderId);

        return new ManualOrderPrintResult(
            true,
            ManualOrderPrintOutcome.Queued,
            ManualOrderPrintMessageKeys.Queued,
            null);
    }

    private static ManualOrderPrintResult AlreadyQueued() =>
        new(false, ManualOrderPrintOutcome.AlreadyQueued, ManualOrderPrintMessageKeys.AlreadyQueued, null);

    private static ManualOrderPrintResult ReprintConfirmationRequired() =>
        new(false, ManualOrderPrintOutcome.ReprintConfirmationRequired, ManualOrderPrintMessageKeys.ReprintConfirmationRequired, null);

    private static bool IsActive(ReceiptJobRow job) =>
        job.Status is PrintJobStatus.Pending or PrintJobStatus.Printing;

    private static OrderReceiptPrintState BuildState(IReadOnlyList<ReceiptJobRow> jobs)
    {
        if (jobs.Count == 0)
            return OrderReceiptPrintState.None;

        var hasActive = jobs.Any(IsActive);
        var canReprint = !hasActive
            && jobs.Any(j => j.Status is PrintJobStatus.Printed or PrintJobStatus.Failed);

        return new OrderReceiptPrintState(jobs[0].Status, hasActive, canReprint, jobs[0].StatusVersion);
    }

    // Newest first by the order-scoped StatusVersion; jobs created before it existed all have 0 and keep the
    // CreatedAt order.
    private static async Task<IReadOnlyList<ReceiptJobRow>> LoadReceiptJobsAsync(
        TenantDbContext db,
        Guid orderId,
        CancellationToken ct) =>
        await db.PrintJobs
            .AsNoTracking()
            .Where(j => j.OrderId == orderId && j.Type == PrintJobType.Receipt)
            .OrderByDescending(j => j.StatusVersion)
            .ThenByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Select(j => new ReceiptJobRow(j.Id, j.Status, j.StatusVersion))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private sealed record ReceiptJobRow(Guid Id, PrintJobStatus Status, long StatusVersion);
}
