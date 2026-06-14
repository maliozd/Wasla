using Wasla.Domain.Enums;

namespace Wasla.Web.Models.Dashboard;

public sealed class DashboardViewModel
{
    public int TodayOrderCount { get; set; }
    public decimal TodayRevenue { get; set; }
    public int ActiveOrderCount { get; set; }
    public int CancelledOrderCount { get; set; }

    public List<RecentOrderRow> RecentOrders { get; set; } = new();
    public List<PlatformSummaryRow> PlatformSummary { get; set; } = new();

    public sealed class RecentOrderRow
    {
        public Guid Id { get; set; }
        public FoodPlatform Platform { get; set; }
        public string ExternalOrderCode { get; set; } = string.Empty;
        public OrderStatus Status { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public DateTime ReceivedAtUtc { get; set; }
    }

    public sealed class PlatformSummaryRow
    {
        public FoodPlatform Platform { get; set; }
        public int Count { get; set; }
        public decimal Revenue { get; set; }
    }
}

