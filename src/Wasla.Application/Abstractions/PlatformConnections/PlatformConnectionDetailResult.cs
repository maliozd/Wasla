using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.PlatformConnections;

public sealed record PlatformConnectionDetailResult(
    Guid Id,
    FoodPlatform Platform,
    string StoreId,
    bool IsActive,
    int SyncIntervalSeconds,
    string? SupplierId,
    string? ExecutorEmail);

