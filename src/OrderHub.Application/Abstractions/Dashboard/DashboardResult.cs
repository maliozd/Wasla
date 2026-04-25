using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Dashboard;

public sealed class DashboardResult
{
    public int TodayOrderCount { get; init; }
    public decimal TodayRevenue { get; init; }
    public int ActiveOrderCount { get; init; }
    public int CancelledOrderCount { get; init; }

    public IReadOnlyList<RecentOrderRow> RecentOrders { get; init; } = Array.Empty<RecentOrderRow>();
    public IReadOnlyList<PlatformSummaryRow> PlatformSummary { get; init; } = Array.Empty<PlatformSummaryRow>();

    public sealed record RecentOrderRow(
        Guid Id,
        FoodPlatform Platform,
        string ExternalOrderCode,
        OrderStatus Status,
        string CustomerName,
        decimal TotalAmount,
        DateTime ReceivedAtUtc);

    public sealed record PlatformSummaryRow(
        FoodPlatform Platform,
        int Count,
        decimal Revenue);
}

