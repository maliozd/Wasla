using Wasla.Contracts.Enums;

namespace Wasla.Contracts.Orders;

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

