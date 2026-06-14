using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// A normalized order, aggregated from a specific food-delivery platform.
/// Exactly one of these exists per (Platform, ExternalOrderId) tuple, enforced
/// by a unique index on IdempotencyKey.
/// </summary>
public class Order : BaseEntity
{
    public FoodPlatform Platform { get; set; }

    /// <summary>
    /// The order's ID in the external platform's system.
    /// </summary>
    public string ExternalOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable order code the restaurant sees (e.g. "YS-12345").
    /// </summary>
    public string ExternalOrderCode { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of "{Platform}:{ExternalOrderId}".
    /// Enforces idempotency on upsert.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public OrderStatus InternalStatus { get; set; } = OrderStatus.New;

    /// <summary>
    /// Raw status string from the platform, kept for debugging and audit.
    /// </summary>
    public string PlatformStatus { get; set; } = string.Empty;

    // --- End-customer (food orderer) data ---
    // CustomerName, CustomerPhone, and CustomerAddress refer to the restaurant's own
    // end customer who placed the order, not the SaaS tenant/business using Wasla.

    /// <summary>
    /// Name of the end customer who placed the food order (not the restaurant tenant).
    /// Keep Customer terminology for order-level recipient fields.
    /// </summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// Phone number of the end customer who placed the food order (not the restaurant tenant).
    /// Keep Customer terminology for order-level recipient fields.
    /// </summary>
    public string CustomerPhone { get; set; } = string.Empty;

    /// <summary>
    /// Delivery or contact address of the end customer who placed the food order (not the restaurant tenant).
    /// Keep Customer terminology for order-level recipient fields.
    /// </summary>
    public string CustomerAddress { get; set; } = string.Empty;

    // --- Money ---

    public decimal TotalAmount { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal ServiceFee { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Unknown;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unknown;

    // --- Timestamps (all UTC) ---

    /// <summary>
    /// When the customer actually placed this order on the platform.
    /// </summary>
    public DateTime CreatedAtPlatform { get; set; }

    /// <summary>
    /// When our worker first received this order, stored in UTC (compare/query using UTC; display in the restaurant timezone in the app).
    /// </summary>
    public DateTime ReceivedAt { get; set; }

    public DateTime? AcceptedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Raw JSON payload from the platform. Priceless when debugging weird cases
    /// or when a platform silently changes their API.
    /// </summary>
    public string RawPayloadJson { get; set; } = string.Empty;

    // --- Relations ---

    public List<OrderItem> Items { get; set; } = new();
}
