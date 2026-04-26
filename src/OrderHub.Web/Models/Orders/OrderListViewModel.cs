using OrderHub.Domain.Enums;

namespace OrderHub.Web.Models.Orders;

public sealed class OrderListViewModel
{
    public OrderFilterViewModel Filters { get; set; } = new();
    public List<Row> Orders { get; set; } = new();

    public int TotalCount { get; set; }

    /// <summary>When no rows, use a simple "no orders" string instead of "no matches for filters" (e.g. default today, no extra filters).</summary>
    public bool UseSimpleNoOrdersMessage { get; set; }

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

