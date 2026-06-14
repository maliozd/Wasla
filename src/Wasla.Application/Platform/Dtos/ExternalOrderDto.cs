using Wasla.Domain.Enums;

namespace Wasla.Application.Platform.Dtos;

public sealed record ExternalOrderDto(
    FoodPlatform Platform,
    string ExternalOrderId,
    string ExternalOrderCode,
    DateTime OrderedAtUtc,
    string CustomerName,
    string CustomerPhone,
    string CustomerAddress,
    decimal Subtotal,
    decimal DeliveryFee,
    decimal ServiceFee,
    decimal Total,
    PaymentMethod PaymentMethod,
    PaymentStatus PaymentStatus,
    string ExternalStatus,
    string RawPayloadJson,
    IReadOnlyCollection<ExternalOrderItemDto> Items);

