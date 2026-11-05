using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Orders;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Services;

public sealed class OrderAutoApproveService : IOrderAutoApproveService
{
    private static readonly Guid TenantOperationalSettingsSingletonId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantDbContextFactory _dbFactory;
    private readonly IOrderActionService _orderActions;
    private readonly IOrderReceiptCreationService _receiptCreation;
    private readonly ILogger<OrderAutoApproveService> _logger;

    public OrderAutoApproveService(
        ITenantDbContextFactory dbFactory,
        IOrderActionService orderActions,
        IOrderReceiptCreationService receiptCreation,
        ILogger<OrderAutoApproveService> logger)
    {
        _dbFactory = dbFactory;
        _orderActions = orderActions;
        _receiptCreation = receiptCreation;
        _logger = logger;
    }

    public async Task ProcessNewlyInsertedOrderAsync(
        Guid tenantId,
        Guid orderId,
        OrderStatus insertedStatus,
        CancellationToken ct)
    {
        if (!IsEligibleForAutoApprove(insertedStatus))
            return;

        await using var db = await _dbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

        var settings = await db.TenantOperationalSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == TenantOperationalSettingsSingletonId, ct)
            .ConfigureAwait(false);

        // A tenant still in Setup keeps every order but accepts nothing automatically. The decision is taken now,
        // when the order arrives; going live later never replays it for orders that arrived during Setup.
        if (settings?.OperationalMode == TenantOperationalMode.Setup)
        {
            _logger.LogDebug(
                "Tenant is in setup mode, skipping auto-approve. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return;
        }

        var autoApproveEnabled = settings?.AutoApproveNewOrders ?? false;

        if (!autoApproveEnabled)
        {
            _logger.LogDebug(
                "AutoApprove disabled, skipping. TenantId={TenantId}, OrderId={OrderId}",
                tenantId,
                orderId);
            return;
        }

        _logger.LogInformation(
            "AutoApprove enabled for new order. TenantId={TenantId}, OrderId={OrderId}",
            tenantId,
            orderId);

        var approveResult = await _orderActions.TryApproveAsync(tenantId, orderId, ct).ConfigureAwait(false);
        if (!approveResult.Succeeded)
        {
            _logger.LogWarning(
                "Order auto-approve failed. TenantId={TenantId}, OrderId={OrderId}, MessageKey={MessageKey}",
                tenantId,
                orderId,
                approveResult.MessageKey);
            return;
        }

        _logger.LogInformation(
            "Order auto-approved successfully. TenantId={TenantId}, OrderId={OrderId}",
            tenantId,
            orderId);

        await _receiptCreation.TryCreateOnOrderAcceptedAsync(tenantId, orderId, ct).ConfigureAwait(false);
    }

    private static bool IsEligibleForAutoApprove(OrderStatus status) =>
        status == OrderStatus.New;
}
