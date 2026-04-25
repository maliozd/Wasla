using OrderHub.Contracts.Enums;

namespace OrderHub.Contracts.Orders;

public record OrderDetailDto(
    Guid Id,
    FoodPlatformDto Platform,
    string ExternalOrderId,
    string CustomerName,
    decimal TotalAmount,
    decimal DeliveryFee,
    decimal ServiceFee,
    string PaymentMethod,
    string PaymentStatus,
    OrderStatusDto InternalStatus,
    string PlatformStatus,
    DateTime CreatedAtPlatform,
    DateTime ReceivedAt,
    DateTime? AcceptedAt,
    DateTime? DeliveredAt,
    DateTime? CancelledAt,
    IReadOnlyList<OrderItemDto> Items);

public record OrderItemDto(
    Guid Id,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    string? Notes,
    IReadOnlyList<OrderItemOptionDto> Options);

public record OrderItemOptionDto(
    Guid Id,
    string Name,
    decimal Price);

