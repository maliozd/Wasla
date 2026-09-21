using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Printing;

namespace Wasla.Infrastructure.Services;

public sealed class PrintJobHistoryService : IPrintJobHistoryService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IReceiptTemplateSettingsService _templateSettings;
    private readonly ILogger<PrintJobHistoryService> _logger;

    public PrintJobHistoryService(
        ITenantDbContextFactory dbFactory,
        IReceiptTemplateSettingsService templateSettings,
        ILogger<PrintJobHistoryService> logger)
    {
        _dbFactory = dbFactory;
        _templateSettings = templateSettings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PrintJobHistoryItemDto>> GetRecentReceiptJobsAsync(
        Guid customerId,
        int limit,
        CancellationToken ct)
    {
        var safeLimit = Math.Clamp(limit, 1, PrintJobHistoryLimits.Max);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var rows = await (
                from j in db.PrintJobs.AsNoTracking()
                where j.Type == PrintJobType.Receipt
                join o in db.Orders.AsNoTracking() on j.OrderId equals o.Id into orderJoin
                from o in orderJoin.DefaultIfEmpty()
                orderby j.CreatedAt descending
                select new
                {
                    j.Id,
                    j.OrderId,
                    j.Status,
                    j.CreatedAt,
                    j.LastAttemptAt,
                    j.PrintedAt,
                    j.AttemptCount,
                    j.ErrorMessage,
                    j.LockedBy,
                    OrderExternalOrderId = o != null ? o.ExternalOrderId : null,
                    OrderExternalOrderCode = o != null ? o.ExternalOrderCode : null,
                    OrderPlatform = o != null ? (FoodPlatform?)o.Platform : null,
                    OrderCustomerName = o != null ? o.CustomerName : null,
                    OrderTotalAmount = o != null ? (decimal?)o.TotalAmount : null
                })
            .Take(safeLimit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new PrintJobHistoryItemDto(
            r.Id,
            r.OrderId,
            NullIfEmpty(r.OrderExternalOrderId),
            NullIfEmpty(r.OrderExternalOrderCode),
            r.OrderPlatform ?? FoodPlatform.Yemeksepeti,
            r.Status,
            r.CreatedAt,
            r.LastAttemptAt,
            r.PrintedAt,
            r.AttemptCount,
            NullIfEmpty(r.ErrorMessage),
            NullIfEmpty(r.LockedBy),
            NullIfEmpty(r.OrderCustomerName),
            r.OrderTotalAmount))
            .ToList();
    }

    public async Task<ReprintReceiptResult> CreateReprintAsync(
        Guid customerId,
        Guid printJobId,
        string? tenantDisplayName,
        CancellationToken ct)
    {
        // Load settings before acquiring the serializable lock, as in the first-print path.
        // The settings service opens a separate tenant context.
        var template = await _templateSettings
            .GetAsync(customerId, tenantDisplayName, null, ct)
            .ConfigureAwait(false);

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        // Same serializable guard the first-receipt path uses, so concurrent reprint requests
        // cannot both pass the active-job check and queue two receipts for one order.
        await using var tx = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        var source = await db.PrintJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == printJobId && j.Type == PrintJobType.Receipt, ct)
            .ConfigureAwait(false);

        if (source is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintJobNotFound", null);
        }

        if (source.Status is PrintJobStatus.Pending or PrintJobStatus.Printing)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintNotAllowed", null);
        }

        if (source.Status is not (PrintJobStatus.Printed or PrintJobStatus.Failed))
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintNotAllowed", null);
        }

        var hasActiveJob = await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                j => j.OrderId == source.OrderId
                     && j.Type == PrintJobType.Receipt
                     && (j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing),
                ct)
            .ConfigureAwait(false);

        if (hasActiveJob)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintAlreadyPending", null);
        }

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == source.OrderId, ct)
            .ConfigureAwait(false);

        if (order is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintOrderNotFound", null);
        }

        var copyCount = Math.Clamp(source.CopyCount, 1, 3);
        var payloadJson = ReceiptPayloadBuilder.Build(order, tenantDisplayName, template);

        var nowUtc = DateTime.UtcNow;

        var job = new PrintJob
        {
            OrderId = source.OrderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Pending,
            CopyCount = copyCount,
            PayloadJson = payloadJson,
            AttemptCount = 0,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
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

            _logger.LogWarning(
                ex,
                "Receipt reprint job creation failed. CustomerId={CustomerId}, SourceJobId={SourceJobId}, OrderId={OrderId}",
                customerId,
                printJobId,
                source.OrderId);
            return new ReprintReceiptResult(false, "PrintBridge.ReprintFailed", null);
        }

        _logger.LogInformation(
            "Receipt reprint job created. CustomerId={CustomerId}, SourceJobId={SourceJobId}, NewJobId={NewJobId}, OrderId={OrderId}",
            customerId,
            printJobId,
            job.Id,
            source.OrderId);

        return new ReprintReceiptResult(true, "PrintBridge.ReprintCreated", job.Id);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
