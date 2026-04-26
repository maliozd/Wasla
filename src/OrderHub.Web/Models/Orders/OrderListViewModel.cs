using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.Orders;

public sealed class OrderListViewModel
{
    public OrderFilterViewModel Filters { get; set; } = new();
    public List<Row> Orders { get; set; } = new();

    public int TotalCount { get; set; }

    /// <summary>Max ReceivedAt in the customer database (UTC), for client new-order checks.</summary>
    public DateTime? LatestReceivedAtUtc { get; set; }

    public sealed class Row
    {
        public Guid Id { get; set; }
        public FoodPlatform Platform { get; set; }
        public string ExternalOrderCode { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
    }
}

