using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.PlatformConnections;

public sealed record PlatformConnectionResult(
    Guid Id,
    FoodPlatform Platform,
    string StoreId,
    bool IsActive,
    DateTime? LastSyncAttemptUtc,
    DateTime? LastSuccessfulSyncUtc,
    int ConsecutiveFailures,
    DateTime? CircuitOpenUntilUtc,
    int SyncIntervalSeconds);

