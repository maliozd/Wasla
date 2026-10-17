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

    /// <summary>
    /// Order-level instruction from the end customer, such as a delivery or preparation request.
    /// This is not an item note. Item instructions stay on <see cref="OrderItem.Notes"/>.
    /// </summary>
    public string? CustomerNote { get; set; }

    // --- Money ---

    public decimal TotalAmount { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal ServiceFee { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Unknown;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unknown;

    // --- Timestamps (all UTC) ---

    /// <summary>
    /// When the order was created on the platform, as the provider reported it (Trendyol GO <c>packageCreationDate</c>).
    /// Null when the provider sent no plausible creation time: Wasla never fills it from its own clock.
    /// </summary>
    public DateTime? CreatedAtPlatform { get; set; }

    /// <summary>
    /// The provider's latest modification time for the order (Trendyol GO <c>packageModificationDate</c>), when it sends one.
    /// </summary>
    public DateTime? PlatformModifiedAt { get; set; }

    /// <summary>
    /// When Wasla first stored this order. Set once on insert; later synchronization never changes it.
    /// </summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>
    /// The operational order time (WAS-125): <see cref="CreatedAtPlatform"/> when the provider supplied it, otherwise
    /// <see cref="ReceivedAt"/>. Computed and stored by the database; Orders, Dashboard and Live Screen sort, filter
    /// and count by it. Read <see cref="CreatedAtPlatform"/> to know whether it is the provider's time.
    /// </summary>
    public DateTime OrderedAt { get; private set; }

    public DateTime? AcceptedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Raw JSON payload from the platform. Priceless when debugging weird cases
    /// or when a platform silently changes their API.
    /// </summary>
    public string RawPayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Per-order change sequence for live Live Screen invalidation (WAS-24). 1 when the order is stored; every later
    /// write that changes the order adds one with an atomic <c>ChangeVersion + 1</c> in the same transaction as the
    /// change, so it only grows, two changes never share a value, and a snapshot that shows version N includes every
    /// change up to N. Orders stored before it existed have 0 until their next change.
    /// </summary>
    public long ChangeVersion { get; set; }

    // --- Relations ---

    public List<OrderItem> Items { get; set; } = new();
}
