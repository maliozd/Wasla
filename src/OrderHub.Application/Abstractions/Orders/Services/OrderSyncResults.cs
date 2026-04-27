using OrderHub.Domain.Enums;

namespace OrderHub.Application.Abstractions.Orders.Services;

public sealed record OrderSyncCycleTotals(
    int CustomerCount,
    int ConnectionCount,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    int FailedConnections);

public sealed record OrderSyncCustomerResult(
    Guid CustomerId,
    int ConnectionCount,
    int FetchedCount,
    int InsertedCount,
    int UpdatedCount,
    int SkippedCount,
    int UnchangedCount,
    int FailedConnections);

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
    bool IsFailed);

public sealed record OrderUpsertResult(
    bool Inserted,
    bool Updated,
    bool Skipped,
    bool Unchanged,
    string? ExternalOrderId);

