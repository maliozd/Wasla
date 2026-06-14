using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Customer;

public class OrderItemOption : BaseEntity
{
    public Guid OrderItemId { get; set; }
    public OrderItem? OrderItem { get; set; }

    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
