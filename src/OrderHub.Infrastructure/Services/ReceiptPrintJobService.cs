using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Domain.Entities.Customer;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Customer;
using OrderHub.Infrastructure.Printing;

namespace OrderHub.Infrastructure.Services;

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
    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly ILogger<ReceiptPrintJobService> _logger;

    public ReceiptPrintJobService(
        ICustomerDbContextFactory dbFactory,
        ILogger<ReceiptPrintJobService> logger)
    {
        _dbFactory = dbFactory;
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

        var exists = await db.PrintJobs
            .AsNoTracking()
            .AnyAsync(
                p => p.OrderId == orderId && p.Type == PrintJobType.Receipt,
                ct)
            .ConfigureAwait(false);

        if (exists)
        {
            _logger.LogInformation(
                "Receipt PrintJob skipped because it already exists. CustomerId={CustomerId}, OrderId={OrderId}",
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

        var job = new PrintJob
        {
            OrderId = orderId,
            Type = PrintJobType.Receipt,
            Status = PrintJobStatus.Pending,
            CopyCount = safeCopyCount,
            PayloadJson = ReceiptPayloadBuilder.Build(order, tenantDisplayName),
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
