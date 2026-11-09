using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Domain.Entities.TenantDb;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Printing;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Infrastructure.Services;

public interface IReceiptPrintJobService
{
    /// <summary>Creates a receipt unless one is active: automatic printing, and a print the operator confirmed as another copy.</summary>
    Task<bool> TryCreateReceiptJobAsync(
        Guid tenantId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct);

    /// <summary>
    /// A first print: creates a receipt only while the order has none active and none finished, decided inside the
    /// transaction that inserts it. Declining means nothing was created.
    /// </summary>
    Task<bool> TryCreateFirstReceiptJobAsync(
        Guid tenantId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct);
}

public sealed class ReceiptPrintJobService : IReceiptPrintJobService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IReceiptTemplateSettingsService _templateSettings;
    private readonly ITenantChangeNotifier _notifier;
    private readonly ILogger<ReceiptPrintJobService> _logger;

    public ReceiptPrintJobService(
        ITenantDbContextFactory dbFactory,
        IReceiptTemplateSettingsService templateSettings,
        ITenantChangeNotifier notifier,
        ILogger<ReceiptPrintJobService> logger)
    {
        _dbFactory = dbFactory;
        _templateSettings = templateSettings;
        _notifier = notifier;
        _logger = logger;
    }

    public Task<bool> TryCreateReceiptJobAsync(
        Guid tenantId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct) =>
        CreateAsync(tenantId, orderId, copyCount, tenantDisplayName, firstReceiptOnly: false, ct);

    public Task<bool> TryCreateFirstReceiptJobAsync(
        Guid tenantId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct) =>
        CreateAsync(tenantId, orderId, copyCount, tenantDisplayName, firstReceiptOnly: true, ct);

    private async Task<bool> CreateAsync(
        Guid tenantId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        bool firstReceiptOnly,
        CancellationToken ct)
    {
        // Read template settings on a separate connection before taking the serializable lock.
        // Holding that lock while opening a second tenant context can deadlock concurrent first-prints.
        var template = await _templateSettings
            .GetAsync(tenantId, tenantDisplayName, null, ct)
            .ConfigureAwait(false);

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        await using var tx = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var hasActiveJob = await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                p => p.OrderId == orderId
                     && p.Type == PrintJobType.Receipt
                     && (p.Status == PrintJobStatus.Pending || p.Status == PrintJobStatus.Printing),
                ct)
            .ConfigureAwait(false);

        if (hasActiveJob)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Receipt PrintJob skipped because an active job already exists. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return false;
        }

        // Another physical copy needs the operator's confirmation. Decided under the same lock as the insert: a receipt
        // that finished after the caller's own read is seen here, and one written from now on waits for this transaction.
        if (firstReceiptOnly
            && await db.PrintJobs
                .AsNoTracking()
                .AnyAsync(
                    p => p.OrderId == orderId
                         && p.Type == PrintJobType.Receipt
                         && (p.Status == PrintJobStatus.Printed || p.Status == PrintJobStatus.Failed),
                    ct)
                .ConfigureAwait(false))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "First receipt PrintJob skipped because a receipt has already finished. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return false;
        }

        // The receipt lists items in the order every screen shows them.
        var order = await db.Orders
            .AsNoTracking()
            .IncludeItemsInSequence()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            .ConfigureAwait(false);

        if (order is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            _logger.LogWarning(
                "Receipt PrintJob creation skipped because order was not found. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return false;
        }

        var safeCopyCount = Math.Clamp(copyCount, 1, 3);
        var nowUtc = DateTime.UtcNow;
        var statusVersion = await PrintJobStatusVersions.NextForNewJobAsync(db, orderId, ct).ConfigureAwait(false);

        var job = new PrintJob
        {
            OrderId = orderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Pending,
            CopyCount = safeCopyCount,
            PayloadJson = ReceiptPayloadBuilder.Build(order, tenantDisplayName, template),
            AttemptCount = 0,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            StatusVersion = statusVersion
        };

        db.PrintJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                ex,
                "Receipt PrintJob skipped because a duplicate was created concurrently. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return false;
        }

        _logger.LogInformation(
            "Receipt PrintJob created. TenantId={TenantId}, OrderId={OrderId}, PrintJobId={PrintJobId}, CopyCount={CopyCount}",
            tenantId,
            orderId,
            job.Id,
            safeCopyCount);

        await TenantChangePublisher.PublishAsync(
                _notifier,
                tenantId,
                new PrintJobChanged(orderId, job.Id, job.Status, job.StatusVersion),
                _logger)
            .ConfigureAwait(false);

        return true;
    }
}
