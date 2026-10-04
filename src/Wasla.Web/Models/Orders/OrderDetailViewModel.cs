using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Orders;

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
    public string? CustomerNote { get; set; }

    public decimal TotalAmount { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal ServiceFee { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public DateTime CreatedAtPlatformUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime ReceivedAtLocal { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? AcceptedAtLocal { get; set; }

    public string BackUrl { get; set; } = "/orders";

    /// <summary>A receipt job for this order is Pending or Printing, so manual printing is blocked.</summary>
    public bool ReceiptPrintInProgress { get; set; }

    /// <summary>A previous receipt job finished as Printed or Failed, so the action becomes a reprint.</summary>
    public bool ReceiptCanReprint { get; set; }

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

