using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Customer;

public class OrderItem : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Zero-based position of this line in the provider's list, as last written by order sync. Every screen and receipt
    /// shows items by Sequence, then <see cref="BaseEntity.CreatedAt"/>, then Id; rows stored before positions were kept
    /// all have 0 and so keep that fallback order.
    /// </summary>
    public int Sequence { get; set; }

    public List<OrderItemOption> Options { get; set; } = new();
}
