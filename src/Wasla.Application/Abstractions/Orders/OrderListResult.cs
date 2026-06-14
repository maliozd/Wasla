using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public sealed class OrderListResult
{
    public required IReadOnlyList<Row> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }

    public sealed record Row(
        Guid Id,
        FoodPlatform Platform,
        string ExternalOrderId,
        string ExternalOrderCode,
        string CustomerName,
        decimal TotalAmount,
        OrderStatus Status,
        string PlatformStatus,
        DateTime CreatedAtPlatformUtc,
        DateTime ReceivedAtUtc,
        int ItemCount,
        string? FirstProductName);
}

