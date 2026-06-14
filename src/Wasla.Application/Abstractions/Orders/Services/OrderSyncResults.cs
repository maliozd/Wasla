using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Orders.Services;

public sealed record OrderSyncCycleTotals(
    int CustomerCount,
    int ConnectionCount,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    int FailedConnections);

/// <summary>
/// GUID-free connection summary used by the Worker console presentation layer.
/// </summary>
public sealed record OrderSyncConnectionSummary(
    string Platform,
    string StoreId,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    bool IsFailed,
    long ElapsedMs);

public sealed record OrderSyncCustomerResult(
    Guid CustomerId,
    int ConnectionCount,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    int FailedConnections)
{
    /// <summary>Per-connection summaries. Empty when sync was disabled or no connections were due.</summary>
    public IReadOnlyList<OrderSyncConnectionSummary> Connections { get; init; } = [];

    /// <summary>True when the sync was skipped because the tenant has disabled order sync.</summary>
    public bool WasSyncDisabled { get; init; }
}

public sealed record OrderSyncConnectionResult(
    Guid CustomerId,
    Guid ConnectionId,
    FoodPlatform Platform,
    string StoreId,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    bool IsFailed)
{
    public long ElapsedMs { get; init; }
}

public sealed record OrderUpsertResult(
    bool Inserted,
    bool Updated,
    bool Skipped,
    bool Unchanged,
    string? ExternalOrderId);

