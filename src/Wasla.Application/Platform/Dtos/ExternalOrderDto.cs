using Wasla.Domain.Enums;

namespace Wasla.Application.Platform.Dtos;

/// <param name="OrderedAtUtc">
/// The order's creation time on the platform, in UTC, exactly as the provider reported it. Null when the payload has
/// none or it cannot be read: a client must not substitute its own clock (WAS-125).
/// </param>
/// <param name="ModifiedAtUtc">The provider's latest modification time for the order, when the payload has one.</param>
public sealed record ExternalOrderDto(
    FoodPlatform Platform,
    string ExternalOrderId,
    string ExternalOrderCode,
    DateTime? OrderedAtUtc,
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
    IReadOnlyCollection<ExternalOrderItemDto> Items,
    string? CustomerNote = null,
    DateTime? ModifiedAtUtc = null);

