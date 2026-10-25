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

        // Only allow actions while still New to avoid clobbering worker-driven updates. The write itself checks that,
        // so of simultaneous callers only one changes the order.
        var version = await CommitTransitionAsync(db, orderId, OrderStatus.New, newStatus, ct);
        _logger.LogInformation("Order action updated status: orderId={OrderId} newStatus={NewStatus} saved={Saved}", orderId, newStatus, version is not null);
        if (version is not { } changed)
            return false;
        await PublishChangedAsync(customerId, orderId, changed);
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
        if (target is not (OrderStatus.Accepted or OrderStatus.Cancelled))
            return new OrderActionResult(false, "Orders.ActionFailed");

        await using var db = await _dbFactory.CreateAsync(customerId, ct);

        // Claim the order's transition before reading it: another action on the same order, from any process, is
        // refused here and never reaches the provider (WAS-104).
        await using var claim = await OrderActionLock.TryAcquireAsync(db, orderId, ct);
        if (claim is null)
            return Contested(orderId, target);

        var order = await db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return new OrderActionResult(false, "Orders.ActionFailed");

        if (order.InternalStatus != OrderStatus.New)
            return new OrderActionResult(false, "Orders.InvalidStatusForAction");

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

        // From here the action owns the transition. The provider call and the local write that records it no longer
        // follow the request: an aborted request must not leave a provider change without its local status. The
        // provider HTTP clients bound the call with their own timeouts.
        try
        {
            if (target == OrderStatus.Accepted)
            {
                await client.AcceptOrderAsync(connection, order.ExternalOrderId, preparationMinutes: 0, CancellationToken.None);
            }
            else
            {
                await client.RejectOrderAsync(connection, order.ExternalOrderId, Array.Empty<string>(), reasonId: 0, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider action failed. OrderId={OrderId} Platform={Platform} Target={Target}", orderId, order.Platform, target);
            return new OrderActionResult(false, target == OrderStatus.Accepted ? "Orders.ApproveFailed" : "Orders.RejectFailed");
        }

        if (await CommitTransitionAsync(db, orderId, OrderStatus.New, target, CancellationToken.None) is not { } version)
            return StatusChangedAfterProviderCall(orderId, order.Platform, OrderStatus.New, target);

        await PublishChangedAsync(customerId, orderId, version);
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

        await using var claim = await OrderActionLock.TryAcquireAsync(db, orderId, ct);
        if (claim is null)
            return Contested(orderId, nextStatus);

        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
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

        // The action owns the transition: as in ExecuteAsync, the provider call and its local write ignore the request token.
        try
        {
            if (platformCall is not null)
                await platformCall(client, connection, order, CancellationToken.None);
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

        if (await CommitTransitionAsync(db, orderId, requiredCurrent, nextStatus, CancellationToken.None) is not { } version)
            return StatusChangedAfterProviderCall(orderId, order.Platform, requiredCurrent, nextStatus);

        await PublishChangedAsync(customerId, orderId, version);
        return new OrderActionResult(true, successKey);
    }

    /// <summary>
    /// Moves the order from <paramref name="requiredCurrent"/> to <paramref name="nextStatus"/> and raises its
    /// <c>ChangeVersion</c> in one short transaction (provider calls happen before it), and returns the new version.
    /// The update only applies while the order is still in <paramref name="requiredCurrent"/>, so of simultaneous
    /// writers exactly one changes it, and a status Worker sync applied meanwhile is never overwritten. Null when the
    /// order was no longer in that status; then nothing is committed. The transition's timestamp is set only if empty.
    /// </summary>
    private static async Task<long?> CommitTransitionAsync(
        TenantDbContext db,
        Guid orderId,
        OrderStatus requiredCurrent,
        OrderStatus nextStatus,
        CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = db.Orders.Where(o => o.Id == orderId && o.InternalStatus == requiredCurrent);
        var rows = nextStatus switch
        {
            OrderStatus.Accepted => await claimed.ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InternalStatus, nextStatus)
                .SetProperty(o => o.AcceptedAt, o => o.AcceptedAt ?? nowUtc)
                .SetProperty(o => o.UpdatedAt, nowUtc), ct),
            OrderStatus.Cancelled => await claimed.ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InternalStatus, nextStatus)
                .SetProperty(o => o.CancelledAt, o => o.CancelledAt ?? nowUtc)
                .SetProperty(o => o.UpdatedAt, nowUtc), ct),
            OrderStatus.Delivered => await claimed.ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InternalStatus, nextStatus)
                .SetProperty(o => o.DeliveredAt, o => o.DeliveredAt ?? nowUtc)
                .SetProperty(o => o.UpdatedAt, nowUtc), ct),
            _ => await claimed.ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InternalStatus, nextStatus)
                .SetProperty(o => o.UpdatedAt, nowUtc), ct)
        };
        if (rows <= 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var version = await OrderChangeVersions.NextAsync(db, orderId, ct);
        await tx.CommitAsync(ct);
        return version;
    }

    // Another action on the same order holds its transition: this one changes nothing and calls no provider.
    private OrderActionResult Contested(Guid orderId, OrderStatus target)
    {
        _logger.LogInformation(
            "Order action refused: another action on the order is in progress. OrderId={OrderId} Target={Target}",
            orderId,
            target);
        return new OrderActionResult(false, "Orders.InvalidStatusForAction");
    }

    // The provider call succeeded, but the order left the required status meanwhile (Worker sync applied a provider
    // status). The local status the sync wrote stands; this action records and publishes nothing.
    private OrderActionResult StatusChangedAfterProviderCall(Guid orderId, FoodPlatform platform, OrderStatus from, OrderStatus to)
    {
        _logger.LogWarning(
            "Order status changed during the provider call; the local transition was not applied. OrderId={OrderId} Platform={Platform} {From}->{To}",
            orderId,
            platform,
            from,
            to);
        return new OrderActionResult(false, "Orders.InvalidStatusForAction");
    }

    // After the save committed the status change: open Live Screens read their snapshot again. Never fails the action.
    private Task PublishChangedAsync(Guid customerId, Guid orderId, long version) =>
        TenantChangePublisher.PublishAsync(_notifier, customerId, new OrderChanged(orderId, version), _logger);
}

