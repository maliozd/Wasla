using OrderHub.Domain.Common;

namespace OrderHub.Domain.Entities.Customer;

public class OrderItem : BaseEntity
{
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string? Notes { get; set; }

    public List<OrderItemOption> Options { get; set; } = new();
}
