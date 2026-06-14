using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.PlatformConnections;

public sealed record CreatePlatformConnectionCommand(
    FoodPlatform Platform,
    string StoreId,
    string ApiKey,
    string ApiSecret,
    bool IsActive,
    string? SupplierId = null,
    string? ExecutorEmail = null);

