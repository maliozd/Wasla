using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Persistence.Tenant;

namespace Wasla.Infrastructure.Platform.TrendyolGo.Webhooks;

/// <summary>One stored webhook event as the operator sees it: delivery and processing state, never the payload.</summary>
public sealed record TrendyolGoMealWebhookEventView(
    Guid Id,
    DateTime ReceivedAt,
    DateTime LastDeliveredAt,
    int DeliveryCount,
    string EventType,
    string ExternalOrderId,
    ProviderWebhookEventStatus Status,
    int Attempts,
    DateTime NextAttemptAt,
    string? Outcome,
    string? LastError,
    Guid? OrderId,
    DateTime? ProcessedAt)
{
    /// <summary>The matched order's provider order code, when the event was matched to an order.</summary>
    public string? OrderCode { get; init; }

    /// <summary>The matched order's current Wasla status, when the event was matched to an order.</summary>
    public OrderStatus? OrderStatus { get; init; }
}

/// <summary>Read-only inspection of the stage pilot's inbox for the operator (the CLI's <c>meal-webhook-inbox</c>).</summary>
public static class TrendyolGoMealWebhookInspection
{
    /// <summary>The newest events first, without payloads: no customer data leaves the tenant database.</summary>
    public static async Task<List<TrendyolGoMealWebhookEventView>> ListAsync(TenantDbContext db, int limit, CancellationToken ct)
    {
        var events = await db.ProviderWebhookEvents.AsNoTracking()
            .Where(e => e.Platform == FoodPlatform.TrendyolYemek)
            .OrderByDescending(e => e.ReceivedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new TrendyolGoMealWebhookEventView(
                e.Id,
                e.ReceivedAt,
                e.LastDeliveredAt,
                e.DeliveryCount,
                e.EventType,
                e.ExternalOrderId,
                e.Status,
                e.Attempts,
                e.NextAttemptAt,
                e.Outcome,
                e.LastError,
                e.OrderId,
                e.ProcessedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The matched orders' code and status only: never customer fields.
        var orderIds = events.Where(e => e.OrderId is not null).Select(e => e.OrderId!.Value).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.ExternalOrderCode, o.InternalStatus })
            .ToDictionaryAsync(o => o.Id, ct)
            .ConfigureAwait(false);

        return events
            .Select(e => e.OrderId is { } id && orders.TryGetValue(id, out var order)
                ? e with { OrderCode = order.ExternalOrderCode, OrderStatus = order.InternalStatus }
                : e)
            .ToList();
    }
}
