using Wasla.Contracts.Enums;

namespace Wasla.Contracts.Dashboard;

/// <summary>
/// The order report of <c>GET /api/dashboard/report</c> (WAS-182, docs/orders/reporting.md). Amounts are provider order
/// values in <see cref="Currency"/>, not revenue, payouts or earnings. No order customer data.
/// </summary>
public record OrderReportDto(
    OrderReportPeriodDto Period,
    FoodPlatformDto? Platform,
    string Currency,
    DateTime GeneratedAtUtc,
    OrderReportFiguresDto Summary,
    IReadOnlyList<OrderReportPlatformDto> Platforms,
    IReadOnlyList<OrderReportDayDto> Days,
    IReadOnlyList<OrderReportCancellationReasonDto> CancellationReasons);

/// <summary>
/// A reason code on <see cref="Platform"/>'s own list and how many cancelled or rejected orders carry it (WAS-200). A
/// platform's rows plus its <see cref="OrderReportFiguresDto.CancellationReasonMissingOrderCount"/> equal its cancelled count.
/// </summary>
public record OrderReportCancellationReasonDto(FoodPlatformDto Platform, int ReasonCode, int OrderCount);

/// <summary>
/// The local days covered (inclusive) and their UTC bounds (<c>[UtcStartInclusive, UtcEndExclusive)</c>).
/// <see cref="IsComplete"/> is false while the period includes today.
/// </summary>
public record OrderReportPeriodDto(
    string Preset,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTime UtcStartInclusive,
    DateTime UtcEndExclusive,
    string TimeZone,
    int DayCount,
    bool IsComplete);

/// <summary>
/// Figures of one cohort. <see cref="OrderCount"/> equals the sum of the five status counts;
/// <see cref="AverageCompletedOrderValue"/> is null when no order was completed. <see cref="CancelledOrderCount"/> equals
/// <see cref="KnownRejectedOrderCount"/> + <see cref="KnownCancelledOrderCount"/> + <see cref="UnknownOutcomeOrderCount"/>;
/// <see cref="RestaurantOriginUnknownOutcomeOrderCount"/> is part of the unknown count.
/// </summary>
public record OrderReportFiguresDto(
    int OrderCount,
    decimal OrderValue,
    int CompletedOrderCount,
    decimal CompletedOrderValue,
    decimal? AverageCompletedOrderValue,
    int CancelledOrderCount,
    int FailedOrderCount,
    int InProgressOrderCount,
    int OtherStatusOrderCount,
    int KnownRejectedOrderCount,
    int KnownCancelledOrderCount,
    int UnknownOutcomeOrderCount,
    int RestaurantOriginUnknownOutcomeOrderCount,
    int CancellationReasonMissingOrderCount);

public record OrderReportPlatformDto(FoodPlatformDto Platform, OrderReportFiguresDto Figures);

public record OrderReportDayDto(DateOnly Date, DateTime UtcStartInclusive, DateTime UtcEndExclusive, OrderReportFiguresDto Figures);
