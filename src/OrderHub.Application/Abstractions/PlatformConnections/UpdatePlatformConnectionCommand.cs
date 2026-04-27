using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.PlatformConnections;

public sealed record UpdatePlatformConnectionCommand(
    FoodPlatform Platform,
    string StoreId,
    bool IsActive,
    int? SyncIntervalSeconds,
    string? SupplierId,
    string? ExecutorEmail,
    string? ApiKey,
    string? ApiSecret);

