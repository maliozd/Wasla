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
        Guid customerId,
        Guid orderId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
        return BuildState(await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false));
    }

    public async Task<ManualOrderPrintResult> QueueReceiptPrintAsync(
        Guid customerId,
        Guid orderId,
        string? tenantDisplayName,
        CancellationToken ct)
    {
        int copyCount;
        ReceiptJobRow? reprintSource;

        await using (var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false))
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
                .CreateReprintAsync(customerId, reprintSource.Id, tenantDisplayName, ct)
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
                "Manual receipt reprint failed. CustomerId={CustomerId}, OrderId={OrderId}, SourceJobId={SourceJobId}, MessageKey={MessageKey}",
                customerId,
                orderId,
                reprintSource.Id,
                reprint.MessageKey);

            return new ManualOrderPrintResult(
                false,
                ManualOrderPrintOutcome.Failed,
                reprint.MessageKey,
                null);
        }

        var created = await _receiptJobs
            .TryCreateReceiptJobAsync(customerId, orderId, copyCount, tenantDisplayName, ct)
            .ConfigureAwait(false);

        if (!created)
        {
            // TryCreateReceiptJobAsync only declines when an active job already exists (including the
            // serializable-transaction duplicate race), so re-read to report the real state.
            await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);
            var jobs = await LoadReceiptJobsAsync(db, orderId, ct).ConfigureAwait(false);

            if (jobs.Any(IsActive))
                return AlreadyQueued();

            _logger.LogWarning(
                "Manual receipt print could not be queued. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);

            return new ManualOrderPrintResult(
                false,
                ManualOrderPrintOutcome.Failed,
                ManualOrderPrintMessageKeys.Failed,
                null);
        }

        _logger.LogInformation(
            "Manual receipt print queued. CustomerId={CustomerId}, OrderId={OrderId}",
            customerId,
            orderId);

        return new ManualOrderPrintResult(
            true,
            ManualOrderPrintOutcome.Queued,
            ManualOrderPrintMessageKeys.Queued,
            null);
    }

    private static ManualOrderPrintResult AlreadyQueued() =>
        new(false, ManualOrderPrintOutcome.AlreadyQueued, ManualOrderPrintMessageKeys.AlreadyQueued, null);

    private static bool IsActive(ReceiptJobRow job) =>
        job.Status is PrintJobStatus.Pending or PrintJobStatus.Printing;

    private static OrderReceiptPrintState BuildState(IReadOnlyList<ReceiptJobRow> jobs)
    {
        if (jobs.Count == 0)
            return OrderReceiptPrintState.None;

        var hasActive = jobs.Any(IsActive);
        var canReprint = !hasActive
            && jobs.Any(j => j.Status is PrintJobStatus.Printed or PrintJobStatus.Failed);

        return new OrderReceiptPrintState(jobs[0].Status, hasActive, canReprint);
    }

    private static async Task<IReadOnlyList<ReceiptJobRow>> LoadReceiptJobsAsync(
        TenantDbContext db,
        Guid orderId,
        CancellationToken ct) =>
        await db.PrintJobs
            .AsNoTracking()
            .Where(j => j.OrderId == orderId && j.Type == PrintJobType.Receipt)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Select(j => new ReceiptJobRow(j.Id, j.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private sealed record ReceiptJobRow(Guid Id, PrintJobStatus Status);
}
