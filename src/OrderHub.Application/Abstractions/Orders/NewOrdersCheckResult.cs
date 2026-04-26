namespace OrderHub.Application.Abstractions.Orders;

public sealed class NewOrdersCheckResult
{
    public required bool HasNewOrders { get; init; }
    public required int NewOrderCount { get; init; }
    public required IReadOnlyList<Guid> NewOrderIds { get; init; }
    public DateTime? LatestReceivedAtUtc { get; init; }
}
