using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.Orders;

public sealed class OrderDetailViewModel
{
    public Guid Id { get; set; }
    public FoodPlatform Platform { get; set; }
    public string ExternalOrderId { get; set; } = string.Empty;
    public string ExternalOrderCode { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerAddress { get; set; } = string.Empty;

    public decimal TotalAmount { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal ServiceFee { get; set; }

    public DateTime CreatedAtPlatformUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }

    public List<ItemRow> Items { get; set; } = new();

    public sealed class ItemRow
    {
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
        public string? Notes { get; set; }
        public List<OptionRow> Options { get; set; } = new();
    }

    public sealed class OptionRow
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }
}

