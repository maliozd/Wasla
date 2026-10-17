using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public sealed class OrderDetailResult
{
    public required Guid Id { get; init; }
    public required FoodPlatform Platform { get; init; }
    public required string ExternalOrderId { get; init; }
    public required string ExternalOrderCode { get; init; }

    public required string CustomerName { get; init; }
    public required string CustomerPhone { get; init; }
    public required string CustomerAddress { get; init; }
    public string? CustomerNote { get; init; }

    public required decimal TotalAmount { get; init; }
    public required decimal DeliveryFee { get; init; }
    public required decimal ServiceFee { get; init; }

    public required PaymentMethod PaymentMethod { get; init; }
    public required PaymentStatus PaymentStatus { get; init; }

    public required OrderStatus Status { get; init; }
    public required string PlatformStatus { get; init; }

    /// <summary>The provider's creation time; null when it sent none (WAS-125).</summary>
    public required DateTime? CreatedAtPlatformUtc { get; init; }

    /// <summary>The provider's latest modification time, when it sends one.</summary>
    public required DateTime? PlatformModifiedAtUtc { get; init; }

    /// <summary>Operational order time: <see cref="CreatedAtPlatformUtc"/>, otherwise <see cref="ReceivedAtUtc"/>.</summary>
    public required DateTime OrderedAtUtc { get; init; }

    /// <summary>When Wasla first stored the order.</summary>
    public required DateTime ReceivedAtUtc { get; init; }

    /// <summary>When Wasla last wrote a change to the order, from synchronization or an operator.</summary>
    public required DateTime LastChangedAtUtc { get; init; }
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

