using OrderHub.Contracts.Enums;
using OrderHub.Contracts.Orders;

namespace OrderHub.Contracts.Dashboard;

public record DashboardSummaryDto(
    int TodayOrderCount,
    decimal TodayRevenue,
    int TodaySyncSuccessCount,
    int TodaySyncTotalCount,
    double SyncSuccessRate,
    IReadOnlyList<PlatformBreakdownDto> PlatformBreakdown,
    IReadOnlyList<OrderListItemDto> RecentOrders);

public record PlatformBreakdownDto(
    FoodPlatformDto Platform,
    int OrderCount,
    decimal Revenue);

