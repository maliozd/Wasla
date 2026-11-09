using Wasla.Application.Reports;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Reports;

/// <summary>
/// The tenant's order report for a resolved filter (WAS-182). Every figure comes from one aggregate query over stored
/// orders, so the summary, platform rows, day rows and days per platform always reconcile. See docs/orders/reporting.md.
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
    IReadOnlyList<OrderReportDayRow> Days,
    IReadOnlyList<OrderReportPlatformDays> PlatformDays,
    IReadOnlyList<OrderReportCancellationReasonRow> CancellationReasons);

/// <summary>
/// A stored reason code and how many cancelled or rejected orders of the cohort carry it (WAS-200). The code belongs to
/// <see cref="Platform"/>'s own list, so codes of different platforms are never added together. Rows are ordered by count
/// descending, then platform and code. A platform's rows plus its
/// <see cref="OrderReportFigures.CancellationReasonMissingOrderCount"/> equal its <see cref="OrderReportFigures.CancelledOrderCount"/>.
/// </summary>
public sealed record OrderReportCancellationReasonRow(FoodPlatform Platform, int ReasonCode, int OrderCount);

/// <summary>One platform with at least one order in the cohort. Rows are ordered by order count, then platform.</summary>
public sealed record OrderReportPlatformRow(FoodPlatform Platform, OrderReportFigures Figures);

/// <summary>
/// The days of one platform of <see cref="OrderReport.Platforms"/>, in the same order, with every local day of the period.
/// Its days sum to that platform's row, and each day summed over the platforms is that day's row of the report.
/// </summary>
public sealed record OrderReportPlatformDays(FoodPlatform Platform, IReadOnlyList<OrderReportDayRow> Days);

/// <summary>One local day of the period with its UTC bounds; days without orders are included with zero figures.</summary>
public sealed record OrderReportDayRow(DateOnly Date, DateTime UtcStartInclusive, DateTime UtcEndExclusive, OrderReportFigures Figures);

/// <summary>
/// The figures of a cohort. <see cref="OrderCount"/> is the sum of the five status counts. Values are sums of
/// <c>Order.TotalAmount</c>. <see cref="CancelledOrderCount"/> is
/// <see cref="KnownRejectedOrderCount"/> + <see cref="KnownCancelledOrderCount"/> + <see cref="UnknownOutcomeOrderCount"/>.
/// </summary>
/// <param name="UnknownOutcomeOrderCount">
/// Cancelled or rejected orders for which Wasla cannot tell a rejection from a cancellation: no stored origin, or
/// <c>RestaurantOnPlatform</c>.
/// </param>
/// <param name="RestaurantOriginUnknownOutcomeOrderCount">
/// The part of <see cref="UnknownOutcomeOrderCount"/> whose origin is known to be the restaurant. A known origin that is
/// still not a known outcome.
/// </param>
/// <param name="CancellationReasonMissingOrderCount">Cancelled or rejected orders with no stored reason code.</param>
public sealed record OrderReportFigures(
    int OrderCount,
    decimal OrderValue,
    int CompletedOrderCount,
    decimal CompletedOrderValue,
    int CancelledOrderCount,
    int FailedOrderCount,
    int InProgressOrderCount,
    int OtherStatusOrderCount,
    int KnownRejectedOrderCount,
    int KnownCancelledOrderCount,
    int UnknownOutcomeOrderCount,
    int RestaurantOriginUnknownOutcomeOrderCount,
    int CancellationReasonMissingOrderCount)
{
    public static OrderReportFigures Empty { get; } = new(0, 0m, 0, 0m, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Cancelled or rejected orders that have a stored reason code.</summary>
    public int CancellationReasonRecordedOrderCount => CancelledOrderCount - CancellationReasonMissingOrderCount;

    /// <summary>
    /// The share of cancelled or rejected orders that have a stored reason, from 0 to 1. Null when there are none. It
    /// measures what Wasla knows, not how often orders ended with a reason.
    /// </summary>
    public decimal? CancellationReasonCoverage =>
        CancelledOrderCount == 0 ? null : (decimal)CancellationReasonRecordedOrderCount / CancelledOrderCount;

    /// <summary>
    /// <see cref="CompletedOrderValue"/> divided by <see cref="CompletedOrderCount"/>, rounded to 2 decimals with
    /// midpoints away from zero; null when no order was completed.
    /// </summary>
    public decimal? AverageCompletedOrderValue => CompletedOrderCount == 0
        ? null
        : Math.Round(CompletedOrderValue / CompletedOrderCount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The share of the cohort that was cancelled or rejected, from 0 to 1: <see cref="CancelledOrderCount"/> divided by
    /// <see cref="OrderCount"/>, every status included, so orders still in progress count in the denominator. Null when
    /// the cohort is empty.
    /// </summary>
    public decimal? CancellationRate => OrderCount == 0 ? null : (decimal)CancelledOrderCount / OrderCount;

    /// <summary>
    /// The figures of <paramref name="count"/> orders in one stored status with a total of <paramref name="value"/>, and,
    /// for cancelled or rejected orders, how they ended as far as stored: <paramref name="origin"/> and whether they carry
    /// a reason code.
    /// </summary>
    public static OrderReportFigures ForStatus(
        OrderStatus status, int count, decimal value, OrderCancellationOrigin? origin = null, bool hasReason = false) =>
        OrderReportStatuses.Classify(status) switch
        {
            OrderReportStatusGroup.Completed => new(count, value, count, value, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            OrderReportStatusGroup.Cancelled => ForCancelled(count, value, origin, hasReason),
            OrderReportStatusGroup.Failed => new(count, value, 0, 0m, 0, count, 0, 0, 0, 0, 0, 0, 0),
            OrderReportStatusGroup.InProgress => new(count, value, 0, 0m, 0, 0, count, 0, 0, 0, 0, 0, 0),
            _ => new(count, value, 0, 0m, 0, 0, 0, count, 0, 0, 0, 0, 0)
        };

    private static OrderReportFigures ForCancelled(int count, decimal value, OrderCancellationOrigin? origin, bool hasReason)
    {
        var outcome = OrderReportStatuses.ClassifyCancellation(origin);
        return new(
            count, value, 0, 0m, count, 0, 0, 0,
            outcome == OrderReportCancellationOutcome.KnownRejected ? count : 0,
            outcome == OrderReportCancellationOutcome.KnownCancelled ? count : 0,
            outcome == OrderReportCancellationOutcome.Unknown ? count : 0,
            origin == OrderCancellationOrigin.RestaurantOnPlatform ? count : 0,
            hasReason ? 0 : count);
    }

    public static OrderReportFigures operator +(OrderReportFigures a, OrderReportFigures b) => new(
        a.OrderCount + b.OrderCount,
        a.OrderValue + b.OrderValue,
        a.CompletedOrderCount + b.CompletedOrderCount,
        a.CompletedOrderValue + b.CompletedOrderValue,
        a.CancelledOrderCount + b.CancelledOrderCount,
        a.FailedOrderCount + b.FailedOrderCount,
        a.InProgressOrderCount + b.InProgressOrderCount,
        a.OtherStatusOrderCount + b.OtherStatusOrderCount,
        a.KnownRejectedOrderCount + b.KnownRejectedOrderCount,
        a.KnownCancelledOrderCount + b.KnownCancelledOrderCount,
        a.UnknownOutcomeOrderCount + b.UnknownOutcomeOrderCount,
        a.RestaurantOriginUnknownOutcomeOrderCount + b.RestaurantOriginUnknownOutcomeOrderCount,
        a.CancellationReasonMissingOrderCount + b.CancellationReasonMissingOrderCount);
}

/// <summary>What a report can say about whether a cancelled or rejected order was rejected or cancelled.</summary>
public enum OrderReportCancellationOutcome
{
    KnownRejected,
    KnownCancelled,
    Unknown
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

    /// <summary>
    /// <c>OperatorRejected</c> is a known rejection; <c>OperatorCancelled</c> and <c>ProviderOrCustomer</c> are known
    /// cancellations. A missing origin and <c>RestaurantOnPlatform</c> are unknown: the latter proves the restaurant ended
    /// the order but not whether that was a rejection or a cancellation. A stored number no name exists for is unknown.
    /// </summary>
    public static OrderReportCancellationOutcome ClassifyCancellation(OrderCancellationOrigin? origin) => origin switch
    {
        OrderCancellationOrigin.OperatorRejected => OrderReportCancellationOutcome.KnownRejected,
        OrderCancellationOrigin.OperatorCancelled or OrderCancellationOrigin.ProviderOrCustomer => OrderReportCancellationOutcome.KnownCancelled,
        _ => OrderReportCancellationOutcome.Unknown
    };
}
