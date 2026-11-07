using Wasla.Application.Reports;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Reports;

/// <summary>
/// The tenant's order report for a resolved filter (WAS-182). Every figure comes from one aggregate query over stored
/// orders, so the summary, platform rows and day rows always reconcile. See docs/orders/reporting.md.
/// </summary>
public interface IOrderReportService
{
    Task<OrderReport> GetAsync(Guid tenantId, OrderReportFilter filter, CancellationToken ct);
}

/// <summary>
/// An order-time cohort with the orders' current statuses. Amounts are provider order values in
/// <see cref="CurrencyCode"/>, not revenue, payouts or earnings. No order customer data.
/// </summary>
public sealed record OrderReport(
    OrderReportFilter Filter,
    string CurrencyCode,
    DateTime GeneratedAtUtc,
    OrderReportFigures Summary,
    IReadOnlyList<OrderReportPlatformRow> Platforms,
    IReadOnlyList<OrderReportDayRow> Days);

/// <summary>One platform with at least one order in the cohort. Rows are ordered by order count, then platform.</summary>
public sealed record OrderReportPlatformRow(FoodPlatform Platform, OrderReportFigures Figures);

/// <summary>One local day of the period with its UTC bounds; days without orders are included with zero figures.</summary>
public sealed record OrderReportDayRow(DateOnly Date, DateTime UtcStartInclusive, DateTime UtcEndExclusive, OrderReportFigures Figures);

/// <summary>
/// The figures of a cohort. <see cref="OrderCount"/> is the sum of the five status counts. Values are sums of
/// <c>Order.TotalAmount</c>.
/// </summary>
public sealed record OrderReportFigures(
    int OrderCount,
    decimal OrderValue,
    int CompletedOrderCount,
    decimal CompletedOrderValue,
    int CancelledOrderCount,
    int FailedOrderCount,
    int InProgressOrderCount,
    int OtherStatusOrderCount)
{
    public static OrderReportFigures Empty { get; } = new(0, 0m, 0, 0m, 0, 0, 0, 0);

    /// <summary>
    /// <see cref="CompletedOrderValue"/> divided by <see cref="CompletedOrderCount"/>, rounded to 2 decimals with
    /// midpoints away from zero; null when no order was completed.
    /// </summary>
    public decimal? AverageCompletedOrderValue => CompletedOrderCount == 0
        ? null
        : Math.Round(CompletedOrderValue / CompletedOrderCount, 2, MidpointRounding.AwayFromZero);

    /// <summary>The figures of <paramref name="count"/> orders in one stored status with a total of <paramref name="value"/>.</summary>
    public static OrderReportFigures ForStatus(OrderStatus status, int count, decimal value) =>
        OrderReportStatuses.Classify(status) switch
        {
            OrderReportStatusGroup.Completed => new(count, value, count, value, 0, 0, 0, 0),
            OrderReportStatusGroup.Cancelled => new(count, value, 0, 0m, count, 0, 0, 0),
            OrderReportStatusGroup.Failed => new(count, value, 0, 0m, 0, count, 0, 0),
            OrderReportStatusGroup.InProgress => new(count, value, 0, 0m, 0, 0, count, 0),
            _ => new(count, value, 0, 0m, 0, 0, 0, count)
        };

    public static OrderReportFigures operator +(OrderReportFigures a, OrderReportFigures b) => new(
        a.OrderCount + b.OrderCount,
        a.OrderValue + b.OrderValue,
        a.CompletedOrderCount + b.CompletedOrderCount,
        a.CompletedOrderValue + b.CompletedOrderValue,
        a.CancelledOrderCount + b.CancelledOrderCount,
        a.FailedOrderCount + b.FailedOrderCount,
        a.InProgressOrderCount + b.InProgressOrderCount,
        a.OtherStatusOrderCount + b.OtherStatusOrderCount);
}

/// <summary>How a report groups stored statuses.</summary>
public enum OrderReportStatusGroup
{
    InProgress,
    Completed,
    Cancelled,
    Failed,
    Other
}

public static class OrderReportStatuses
{
    /// <summary>
    /// <c>Delivered</c> is completed; <c>Cancelled</c> (which includes rejections) and <c>Failed</c> are their own groups;
    /// <c>New</c>, <c>Preparing</c>, <c>ReadyForPickup</c> and <c>OnTheWay</c> are in progress; anything else (the
    /// retired value 1) is other.
    /// </summary>
    public static OrderReportStatusGroup Classify(OrderStatus status) => status switch
    {
        OrderStatus.Delivered => OrderReportStatusGroup.Completed,
        OrderStatus.Cancelled => OrderReportStatusGroup.Cancelled,
        OrderStatus.Failed => OrderReportStatusGroup.Failed,
        OrderStatus.New or OrderStatus.Preparing or OrderStatus.ReadyForPickup or OrderStatus.OnTheWay => OrderReportStatusGroup.InProgress,
        _ => OrderReportStatusGroup.Other
    };
}
