using System.Text.Json.Serialization;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders;

public sealed record LiveScreenSnapshotResult(
    DateTime ServerTimeUtc,
    IReadOnlyList<LiveScreenOrderDto> Orders,
    int TodayOrderCount,
    int CancelledOrderCount);

public sealed record LiveScreenOrderDto(
    Guid Id,
    string DisplayNumber,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FoodPlatform Platform,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OrderStatus Status,
    DateTime ReceivedAtUtc,
    DateTime? DeliveredAtUtc,
    string CustomerName,
    decimal TotalAmount,
    IReadOnlyList<LiveScreenLineItemDto> Items);

public sealed record LiveScreenLineItemDto(
    string ProductName,
    int Quantity,
    string? Notes);
