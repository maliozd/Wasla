using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderHub.Application.Abstractions.Orders;
using OrderHub.Domain.Enums;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Persistence.Customer;

namespace OrderHub.Infrastructure.Services;

public sealed class OrderAutoApproveService : IOrderAutoApproveService
{
    private static readonly Guid CustomerOperationalSettingsSingletonId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ICustomerDbContextFactory _dbFactory;
    private readonly CentralDbContext _centralDb;
    private readonly IOrderActionService _orderActions;
    private readonly IReceiptPrintJobService _receiptPrintJobs;
    private readonly ILogger<OrderAutoApproveService> _logger;

    public OrderAutoApproveService(
        ICustomerDbContextFactory dbFactory,
        CentralDbContext centralDb,
        IOrderActionService orderActions,
        IReceiptPrintJobService receiptPrintJobs,
        ILogger<OrderAutoApproveService> logger)
    {
        _dbFactory = dbFactory;
        _centralDb = centralDb;
        _orderActions = orderActions;
        _receiptPrintJobs = receiptPrintJobs;
        _logger = logger;
    }

    public async Task ProcessNewlyInsertedOrderAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus insertedStatus,
        CancellationToken ct)
    {
        if (!IsEligibleForAutoApprove(insertedStatus))
            return;

        await using var db = await _dbFactory.CreateAsync(customerId, ct).ConfigureAwait(false);

        var settings = await db.CustomerOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == CustomerOperationalSettingsSingletonId, ct)
            .ConfigureAwait(false);

        var autoApproveEnabled = settings?.AutoApproveNewOrders ?? false;
        var autoPrintEnabled = settings?.AutoPrintReceiptOnAutoApprove ?? false;
        var copyCount = settings?.ReceiptPrintCopyCount ?? 1;

        if (!autoApproveEnabled)
        {
            _logger.LogDebug(
                "AutoApprove disabled, skipping. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return;
        }

        _logger.LogInformation(
            "AutoApprove enabled for new order. CustomerId={CustomerId}, OrderId={OrderId}",
            customerId,
            orderId);

        var approveResult = await _orderActions.TryApproveAsync(customerId, orderId, ct).ConfigureAwait(false);
        if (!approveResult.Succeeded)
        {
            _logger.LogWarning(
                "Order auto-approve failed. CustomerId={CustomerId}, OrderId={OrderId}, MessageKey={MessageKey}",
                customerId,
                orderId,
                approveResult.MessageKey);
            return;
        }

        _logger.LogInformation(
            "Order auto-approved successfully. CustomerId={CustomerId}, OrderId={OrderId}",
            customerId,
            orderId);

        if (!autoPrintEnabled)
        {
            _logger.LogDebug(
                "AutoPrintReceiptOnAutoApprove disabled, skipping PrintJob. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
            return;
        }

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
                "Receipt PrintJob creation failed but sync continued. CustomerId={CustomerId}, OrderId={OrderId}",
                customerId,
                orderId);
        }
    }

    private static bool IsEligibleForAutoApprove(OrderStatus status) =>
        status == OrderStatus.New;
}
