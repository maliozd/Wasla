using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Printing;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Printing;

namespace Wasla.Infrastructure.Services;

public interface IReceiptPrintJobService
{
    Task<bool> TryCreateReceiptJobAsync(
        Guid customerId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct);
}

public sealed class ReceiptPrintJobService : IReceiptPrintJobService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IReceiptTemplateSettingsService _templateSettings;
    private readonly ILogger<ReceiptPrintJobService> _logger;

    public ReceiptPrintJobService(
        ITenantDbContextFactory dbFactory,
        IReceiptTemplateSettingsService templateSettings,
        ILogger<ReceiptPrintJobService> logger)
    {
        _dbFactory = dbFactory;
        _templateSettings = templateSettings;
        _logger = logger;
    }

    public async Task<bool> TryCreateReceiptJobAsync(
        Guid customerId,
        Guid orderId,
        int copyCount,
        string? tenantDisplayName,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

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
            _logger.LogInformation(
                "Receipt PrintJob skipped because an active job already exists. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return false;
        }

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .ThenInclude(i => i.Options)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            .ConfigureAwait(false);

        if (order is null)
        {
            _logger.LogWarning(
                "Receipt PrintJob creation skipped because order was not found. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return false;
        }

        var safeCopyCount = Math.Clamp(copyCount, 1, 3);
        var nowUtc = DateTime.UtcNow;
        var template = await _templateSettings
            .GetAsync(customerId, tenantDisplayName, null, ct)
            .ConfigureAwait(false);

        var job = new PrintJob
        {
            OrderId = orderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Pending,
            CopyCount = safeCopyCount,
            PayloadJson = ReceiptPayloadBuilder.Build(order, tenantDisplayName, template),
            AttemptCount = 0,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

        db.PrintJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(
                ex,
                "Receipt PrintJob skipped because a duplicate was created concurrently. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return false;
        }

        _logger.LogInformation(
            "Receipt PrintJob created. CustomerId={CustomerId}, OrderId={OrderId}, PrintJobId={PrintJobId}, CopyCount={CopyCount}",
            customerId,
            orderId,
            job.Id,
            safeCopyCount);

        return true;
    }
}
