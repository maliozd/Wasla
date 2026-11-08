using Wasla.Application.Abstractions.Reports;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Dashboard;

/// <summary>
/// Today's figures and the latest orders for the Web and API Dashboards. The figures are the shared order report for
/// today (WAS-182, docs/orders/reporting.md), which <see cref="Today"/> carries in full.
/// </summary>
public sealed class DashboardResult
{
    /// <summary>Today's order report; the figures below are read from it.</summary>
    public OrderReport? Today { get; init; }

    /// <summary>Every order placed today, in any status (<see cref="OrderReportFigures.OrderCount"/>).</summary>
    public int TodayOrderCount { get; init; }

    /// <summary>
    /// The total of every order placed today, in any status, cancellations included
    /// (<see cref="OrderReportFigures.OrderValue"/>). An order value, not revenue.
    /// </summary>
    public decimal TodayOrderValue { get; init; }

    /// <summary>Today's orders that are New, Preparing, ReadyForPickup or OnTheWay.</summary>
    public int ActiveOrderCount { get; init; }

    /// <summary>Today's orders that are Cancelled, which includes rejected orders.</summary>
    public int CancelledOrderCount { get; init; }

    public IReadOnlyList<RecentOrderRow> RecentOrders { get; init; } = Array.Empty<RecentOrderRow>();
    public IReadOnlyList<PlatformSummaryRow> PlatformSummary { get; init; } = Array.Empty<PlatformSummaryRow>();

    /// <summary>One of the latest orders of any day, newest order time first.</summary>
    public sealed record RecentOrderRow(
        Guid Id,
        FoodPlatform Platform,
        string ExternalOrderCode,
        OrderStatus Status,
        string CustomerName,
        decimal TotalAmount,
        DateTime OrderedAtUtc,
        bool OrderedAtIsFromPlatform)
    {
        public string ExternalOrderId { get; init; } = string.Empty;
        public string PlatformStatus { get; init; } = string.Empty;
        public DateTime? CreatedAtPlatformUtc { get; init; }
        public DateTime ReceivedAtUtc { get; init; }
    }

    /// <summary>A platform's orders today: their count and total value in any status.</summary>
    public sealed record PlatformSummaryRow(
        FoodPlatform Platform,
        int Count,
        decimal OrderValue);
}

/// <summary>Today's synchronization runs (by start time), for the API Dashboard.</summary>
public sealed record DashboardSyncStatistics(int SuccessCount, int TotalCount)
{
    /// <summary>Successful runs divided by all runs; 0 when there was no run.</summary>
    public double SuccessRate => TotalCount == 0 ? 0d : (double)SuccessCount / TotalCount;
}
