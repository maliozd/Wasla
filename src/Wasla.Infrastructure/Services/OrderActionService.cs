using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.Platform;
using Wasla.Application.Abstractions.Realtime;
using Wasla.Application.Orders;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;
using Wasla.Infrastructure.Realtime;

namespace Wasla.Infrastructure.Services;

public sealed class OrderActionService : IOrderActionService
{
    private readonly ITenantDbContextFactory _dbFactory;
    private readonly ILogger<OrderActionService> _logger;
    private readonly IEnumerable<IFoodPlatformClient> _clients;
    private readonly ITenantChangeNotifier _notifier;

    public OrderActionService(
        ITenantDbContextFactory dbFactory,
        IEnumerable<IFoodPlatformClient> clients,
        ITenantChangeNotifier notifier,
        ILogger<OrderActionService> logger)
    {
        _dbFactory = dbFactory;
        _clients = clients;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<bool> TryUpdateStatusAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus newStatus,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return false;

        // Only allow actions while still New to avoid clobbering worker-driven updates.
        if (order.InternalStatus != OrderStatus.New) return false;

        order.InternalStatus = newStatus;
        if (newStatus == OrderStatus.Accepted) order.AcceptedAt ??= DateTime.UtcNow;
        if (newStatus == OrderStatus.Cancelled) order.CancelledAt ??= DateTime.UtcNow;

        var version = await SaveWithNextVersionAsync(db, order, ct);
        _logger.LogInformation("Order action updated status: orderId={OrderId} newStatus={NewStatus} saved={Saved}", orderId, newStatus, version is not null);
        if (version is not { } changed)
            return false;
        await PublishChangedAsync(customerId, order.Id, changed);
        return true;
    }

    public async Task<OrderActionResult> TryApproveAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        return await ExecuteAsync(customerId, orderId, OrderStatus.Accepted, ct);
    }

    public async Task<OrderActionResult> TryRejectAsync(Guid customerId, Guid orderId, CancellationToken ct)
    {
        return await ExecuteAsync(customerId, orderId, OrderStatus.Cancelled, ct);
    }

    private async Task<OrderActionResult> ExecuteAsync(Guid customerId, Guid orderId, OrderStatus target, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var order = await db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return new OrderActionResult(false, "Orders.ActionFailed");

        if (order.InternalStatus != OrderStatus.New)
            return new OrderActionResult(false, "Orders.InvalidStatusForAction");

        if (target is not (OrderStatus.Accepted or OrderStatus.Cancelled))
            return new OrderActionResult(false, "Orders.ActionFailed");

        // Choose a connection for the platform. We don't persist connection-id on Order yet (MVP),
        // so we use the first active connection for this platform.
        var connection = await db.PlatformConnections
            .AsNoTracking()
            .Where(c => c.IsActive && c.Platform == order.Platform)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (connection is null)
            return new OrderActionResult(false, "Orders.ActionFailed");

        var client = _clients.FirstOrDefault(c => c.Platform == order.Platform);
        if (client is null)
            return new OrderActionResult(false, "Orders.ActionFailed");

        try
        {
            if (target == OrderStatus.Accepted)
            {
                await client.AcceptOrderAsync(connection, order.ExternalOrderId, preparationMinutes: 0, ct);
            }
            else
            {
                await client.RejectOrderAsync(connection, order.ExternalOrderId, Array.Empty<string>(), reasonId: 0, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider action failed. OrderId={OrderId} Platform={Platform} Target={Target}", orderId, order.Platform, target);
            return new OrderActionResult(false, target == OrderStatus.Accepted ? "Orders.ApproveFailed" : "Orders.RejectFailed");
        }

        order.InternalStatus = target;
        if (target == OrderStatus.Accepted) order.AcceptedAt ??= DateTime.UtcNow;
        if (target == OrderStatus.Cancelled) order.CancelledAt ??= DateTime.UtcNow;

        if (await SaveWithNextVersionAsync(db, order, ct) is not { } version)
            return new OrderActionResult(false, "Orders.ActionFailed");

        await PublishChangedAsync(customerId, order.Id, version);
        return new OrderActionResult(true, target == OrderStatus.Accepted ? "Orders.ApproveSuccess" : "Orders.RejectSuccess");
    }

    // EN: Preparing is an internal kitchen/ops step; IFoodPlatformClient has no shared prepare hook yet—per-platform prepare APIs can map here later.
    // TR: Preparing şu an iç mutfak/operasyon adımı; IFoodPlatformClient'ta ortak prepare kancası yok—ileride platforma özel prepare API'leri buraya bağlanabilir.
    public Task<OrderActionResult> MarkPreparingAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
        TransitionLifecycleAsync(
            customerId,
            orderId,
            requiredCurrent: OrderStatus.Accepted,
            nextStatus: OrderStatus.Preparing,
            platformCall: null,
            successKey: "Orders.StartPreparingSuccess",
            ct);

    // EN: Shared interface still uses Trendyol-shaped names; MarkInvoicedAsync here means ReadyForPickup for all implementing platforms (rename deferred).
    // TR: Ortak interface hâlâ Trendyol etkili isimler taşır; burada MarkInvoicedAsync tüm platformlarda ReadyForPickup anlamına gelir (isim değişikliği ertelendi).
    public Task<OrderActionResult> MarkReadyForPickupAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
        TransitionLifecycleAsync(
            customerId,
            orderId,
            requiredCurrent: OrderStatus.Preparing,
            nextStatus: OrderStatus.ReadyForPickup,
            platformCall: static (client, connection, order, token) =>
                client.MarkInvoicedAsync(connection, order.ExternalOrderId, token),
            successKey: "Orders.MarkReadySuccess",
            ct);

    // EN: MarkShippedAsync is the shared courier handoff / OnTheWay step despite the method name.
    // TR: MarkShippedAsync, isim Trendyol etkili olsa da burada ortak kurye teslimi / OnTheWay adımıdır.
    // User command only: platform couriers report pickup through provider sync, so current platforms
    // are refused before any provider call. Trendyol's manual shipped call is kept for a future
    // restaurant-courier fulfillment mode.
    public Task<OrderActionResult> MarkOnTheWayAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
        TransitionLifecycleAsync(
            customerId,
            orderId,
            requiredCurrent: OrderStatus.ReadyForPickup,
            nextStatus: OrderStatus.OnTheWay,
            platformCall: static (client, connection, order, token) =>
                client.MarkShippedAsync(connection, order.ExternalOrderId, token),
            successKey: "Orders.HandToCourierSuccess",
            ct,
            refuseUserCommand: static order => OrderDeliveryPolicy.IsReportedByPlatform(order.Platform)
                ? OrderDeliveryPolicy.UserPickupNotAllowedKey
                : null);

    // User command only. Provider sync applies Delivered through its own merge, which this guard does not touch.
    public Task<OrderActionResult> MarkDeliveredAsync(Guid customerId, Guid orderId, CancellationToken ct) =>
        TransitionLifecycleAsync(
            customerId,
            orderId,
            requiredCurrent: OrderStatus.OnTheWay,
            nextStatus: OrderStatus.Delivered,
            platformCall: static (client, connection, order, token) =>
                client.MarkDeliveredAsync(connection, order.ExternalOrderId, token),
            successKey: "Orders.MarkDeliveredSuccess",
            ct,
            refuseUserCommand: static order => OrderDeliveryPolicy.IsReportedByPlatform(order.Platform)
                ? OrderDeliveryPolicy.UserDeliveryNotAllowedKey
                : null);

    private delegate Task LifecyclePlatformCall(
        IFoodPlatformClient client,
        PlatformConnection connection,
        Order order,
        CancellationToken ct);

    private async Task<OrderActionResult> TransitionLifecycleAsync(
        Guid customerId,
        Guid orderId,
        OrderStatus requiredCurrent,
        OrderStatus nextStatus,
        LifecyclePlatformCall? platformCall,
        string successKey,
        CancellationToken ct,
        Func<Order, string?>? refuseUserCommand = null)
    {
        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return new OrderActionResult(false, "Orders.ActionFailed");

        if (order.InternalStatus != requiredCurrent)
            return new OrderActionResult(false, "Orders.InvalidStatusForAction");

        var refusal = refuseUserCommand?.Invoke(order);
        if (refusal is not null)
            return new OrderActionResult(false, refusal);

        var connection = await db.PlatformConnections
            .AsNoTracking()
            .Where(c => c.IsActive && c.Platform == order.Platform)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (connection is null)
            return new OrderActionResult(false, "Orders.OrderActionFailed");

        var client = _clients.FirstOrDefault(c => c.Platform == order.Platform);
        if (client is null)
            return new OrderActionResult(false, "Orders.OrderActionFailed");

        try
        {
            if (platformCall is not null)
                await platformCall(client, connection, order, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Lifecycle provider action failed. OrderId={OrderId} Platform={Platform} {From}->{To}",
                orderId,
                order.Platform,
                requiredCurrent,
                nextStatus);
            return new OrderActionResult(false, "Orders.OrderActionFailed");
        }

        order.InternalStatus = nextStatus;
        if (nextStatus == OrderStatus.Delivered)
            order.DeliveredAt ??= DateTime.UtcNow;

        if (await SaveWithNextVersionAsync(db, order, ct) is not { } version)
            return new OrderActionResult(false, "Orders.ActionFailed");

        await PublishChangedAsync(customerId, order.Id, version);
        return new OrderActionResult(true, successKey);
    }

    /// <summary>
    /// Saves the order's change and raises its <c>ChangeVersion</c> in one short transaction (provider calls happen
    /// before it), and returns the new version; null when the save changed nothing, in which case nothing is committed.
    /// </summary>
    private static async Task<long?> SaveWithNextVersionAsync(TenantDbContext db, Order order, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.SaveChangesAsync(ct);
        if (rows <= 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var version = await OrderChangeVersions.NextAsync(db, order.Id, ct);
        await tx.CommitAsync(ct);
        return version;
    }

    // After the save committed the status change: open Live Screens read their snapshot again. Never fails the action.
    private Task PublishChangedAsync(Guid customerId, Guid orderId, long version) =>
        TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(orderId, version), _logger);
}

