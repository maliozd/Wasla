using OrderHub.Domain.Enums;

namespace OrderHub.Application.Platform.Dtos;

public sealed record ExternalOrderDto(
    FoodPlatform Platform,
    string ExternalOrderId,
    DateTime OrderedAtUtc,
    string CustomerName,
    decimal Subtotal,
    decimal DeliveryFee,
    decimal Total,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string ExternalStatus,
    string RawPayloadJson,
    IReadOnlyCollection<ExternalOrderItemDto> Items);

