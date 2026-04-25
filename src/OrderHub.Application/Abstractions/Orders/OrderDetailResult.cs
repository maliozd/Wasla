using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Orders;

public sealed class OrderDetailResult
{
    public required Guid Id { get; init; }
    public required FoodPlatform Platform { get; init; }
    public required string ExternalOrderId { get; init; }
    public required string ExternalOrderCode { get; init; }

    public required string CustomerName { get; init; }
    public required string CustomerPhone { get; init; }
    public required string CustomerAddress { get; init; }

    public required decimal TotalAmount { get; init; }
    public required decimal DeliveryFee { get; init; }
    public required decimal ServiceFee { get; init; }

    public required PaymentMethod PaymentMethod { get; init; }
    public required PaymentStatus PaymentStatus { get; init; }

    public required OrderStatus Status { get; init; }
    public required string PlatformStatus { get; init; }

    public required DateTime CreatedAtPlatformUtc { get; init; }
    public required DateTime ReceivedAtUtc { get; init; }
    public required DateTime? AcceptedAtUtc { get; init; }
    public required DateTime? DeliveredAtUtc { get; init; }
    public required DateTime? CancelledAtUtc { get; init; }

    public required IReadOnlyList<ItemResult> Items { get; init; }

    public sealed record ItemResult(
        Guid Id,
        string ProductName,
        int Quantity,
        decimal UnitPrice,
        decimal TotalPrice,
        string? Notes,
        IReadOnlyList<OptionResult> Options);

    public sealed record OptionResult(Guid Id, string Name, decimal Price);
}

