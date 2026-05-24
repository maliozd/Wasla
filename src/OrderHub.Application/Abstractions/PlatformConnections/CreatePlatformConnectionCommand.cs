using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.PlatformConnections;

public sealed record CreatePlatformConnectionCommand(
    FoodPlatform Platform,
    string StoreId,
    string ApiKey,
    string ApiSecret,
    bool IsActive,
    string? SupplierId = null,
    string? ExecutorEmail = null);

