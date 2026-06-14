using Wasla.Contracts.Enums;

namespace Wasla.Contracts.PlatformConnections;

public record PlatformConnectionDto(
    Guid Id,
    FoodPlatformDto Platform,
    string StoreId,
    bool IsActive,
    DateTime? LastSyncAttempt,
    DateTime? LastSuccessfulSync,
    int ConsecutiveFailures,
    DateTime? CircuitOpenUntil,
    int SyncIntervalSeconds);

public class UpdatePlatformConnectionActiveRequest
{
    public bool IsActive { get; set; }
}

