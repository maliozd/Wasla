using OrderHub.Contracts.Enums;

namespace OrderHub.Contracts.Orders;

public record OrderListItemDto(
    Guid Id,
    FoodPlatformDto Platform,
    string ExternalOrderId,
    string CustomerName,
    decimal TotalAmount,
    OrderStatusDto InternalStatus,
    string PlatformStatus,
    DateTime CreatedAtPlatform,
    DateTime ReceivedAt);

