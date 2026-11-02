using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// One provider webhook delivery, stored before the provider is answered and processed afterwards (the durable inbox).
/// Repeated deliveries of the same event share one row through <see cref="DedupKey"/>. The payload holds customer data
/// like an order's raw payload does: it stays in the tenant database and is never logged.
/// </summary>
public class ProviderWebhookEvent : BaseEntity
{
    public FoodPlatform Platform { get; set; }

    /// <summary>The event type the provider's destination path named, for example <c>created</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The provider's package or order id the event is about.</summary>
    public string ExternalOrderId { get; set; } = string.Empty;

    /// <summary>Unique per event: platform, event type, package id and the provider's timestamp (or the body's hash).</summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The provider's <c>timestamp</c>, when the event carries one.</summary>
    public DateTime? EventTimestampUtc { get; set; }

    public DateTime ReceivedAt { get; set; }
    public DateTime LastDeliveredAt { get; set; }

    /// <summary>How many times the provider delivered this event; above one means duplicates or provider retries.</summary>
    public int DeliveryCount { get; set; } = 1;

    public string PayloadJson { get; set; } = string.Empty;

    public ProviderWebhookEventStatus Status { get; set; }

    /// <summary>Processing attempts so far.</summary>
    public int Attempts { get; set; }

    /// <summary>When a pending event may be processed (again).</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>While processing: the claim expires at this time, so a stopped processor's event is taken up again.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>What processing did, for example <c>Reconciled</c> or <c>StatusApplied</c>; never customer data.</summary>
    public string? Outcome { get; set; }

    /// <summary>A sanitized failure message: no payload, credentials or customer data.</summary>
    public string? LastError { get; set; }

    /// <summary>The order the event was matched to, when there is one.</summary>
    public Guid? OrderId { get; set; }

    public DateTime? ProcessedAt { get; set; }
}
