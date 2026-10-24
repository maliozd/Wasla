using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Customer;

public class OrderItemOption : BaseEntity
{
    public Guid OrderItemId { get; set; }
    public OrderItem? OrderItem { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }

    /// <summary>Zero-based position within its item's options in the provider's list; ordered like <see cref="OrderItem.Sequence"/>.</summary>
    public int Sequence { get; set; }
}
