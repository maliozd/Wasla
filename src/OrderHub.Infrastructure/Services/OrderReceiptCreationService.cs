using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderReceiptCreationService : IOrderReceiptCreationService
{
    private static readonly Guid TenantOperationalSettingsSingletonId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly CentralDbContext _centralDb;
    private readonly IReceiptPrintJobService _receiptPrintJobs;
    private readonly ILogger<OrderReceiptCreationService> _logger;

    public OrderReceiptCreationService(
        ITenantDbContextFactory dbFactory,
        CentralDbContext centralDb,
        IReceiptPrintJobService receiptPrintJobs,
        ILogger<OrderReceiptCreationService> logger)
    {
        _dbFactory = dbFactory;
        _centralDb = centralDb;
        _receiptPrintJobs = receiptPrintJobs;
        _logger = logger;
    }

    public async Task TryCreateOnOrderAcceptedAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var settings = await db.TenantOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TenantOperationalSettingsSingletonId, ct)
            .ConfigureAwait(false);

        if (!(settings?.AutoPrintReceiptOnAutoApprove ?? false))
        {
            _logger.LogDebug(
                "Receipt creation timing is not OnAccepted, skipping PrintJob. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return;
        }

        var copyCount = settings?.ReceiptPrintCopyCount ?? 1;

        try
        {
            var tenantDisplayName = await _centralDb.Customers
                .AsNoTracking()
                .Where(c => c.Id == customerId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            await _receiptPrintJobs.TryCreateReceiptJobAsync(
                customerId,
                orderId,
                copyCount,
                tenantDisplayName,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Receipt PrintJob creation failed. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
        }
    }
}
