using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Admin;

/// <summary>
/// Read-only central operations data for the Admin Tenant Operations Center. Every method reads CentralDb only and
/// never opens a tenant database, so the overview and the tenant list stay usable when a tenant database is down.
/// </summary>
public interface ICentralAdminTenantOperationsService
{
    Task<TenantOperationsOverview> GetOverviewAsync(CancellationToken ct);

    Task<TenantListPage> GetTenantListAsync(TenantListQuery query, CancellationToken ct);

    /// <summary>The central record of one tenant, or null when no tenant has that id.</summary>
    Task<TenantOperationsDetail?> GetTenantDetailAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Reads the operational state of one tenant database. Read-only: it applies no migration and writes nothing.
/// It never throws for an unavailable database; the failure is returned as <see cref="TenantDatabaseState"/>.
/// Only the caller's own cancellation is thrown, as <see cref="OperationCanceledException"/>.
/// </summary>
public interface ITenantOperationalHealthReader
{
    Task<TenantOperationalHealth> ReadAsync(Guid tenantId, CancellationToken ct);
}

// Overview ---------------------------------------------------------------------------------------

public sealed record TenantOperationsOverview(
    TenantRegistryCounts Tenants,
    RegistrationPipelineCounts Registrations,
    PrintBridgeFleetCounts PrintBridge,
    IReadOnlyList<RecentTenantItem> RecentTenants,
    DateTime ReadAtUtc);

/// <param name="MigrationFailed">Tenants whose last recorded tenant-database migration failed (CentralDb record).</param>
/// <param name="MigrationNotRecorded">Tenants with no recorded migration result.</param>
public sealed record TenantRegistryCounts(
    int Total,
    int Active,
    int Inactive,
    int MigrationFailed,
    int MigrationNotRecorded);

/// <param name="AwaitingProvisioning">Paid registrations without a tenant yet (status PaymentSucceeded).</param>
/// <param name="OldestAwaitingProvisioningSinceUtc">Payment time of the longest-waiting paid registration.</param>
public sealed record RegistrationPipelineCounts(
    int AwaitingPayment,
    int AwaitingProvisioning,
    DateTime? OldestAwaitingProvisioningSinceUtc,
    int PaymentFailed,
    int ExpiredOrCancelled,
    int Provisioned);

/// <summary>Registered, not removed Print Bridge devices across all tenants, by the canonical presence thresholds.</summary>
public sealed record PrintBridgeFleetCounts(
    int Online,
    int Stale,
    int Offline,
    int NeverConnected,
    int Disabled)
{
    public int Active => Online + Stale + Offline + NeverConnected;
}

public sealed record RecentTenantItem(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTime CreatedAtUtc);

// Tenant list ------------------------------------------------------------------------------------

public enum TenantListStatusFilter
{
    All = 0,
    Active = 1,
    Inactive = 2
}

public enum TenantMigrationRecordFilter
{
    All = 0,
    Succeeded = 1,
    Failed = 2,
    NotRecorded = 3
}

public enum TenantListSortField
{
    CreatedAt = 0,
    Name = 1,
    Slug = 2,
    UpdatedAt = 3,
    Status = 4
}

public enum TenantListSortDirection
{
    Descending = 0,
    Ascending = 1
}

/// <summary>
/// A normalized tenant-list request. Build it with <see cref="Create"/> from browser input: every value is checked
/// against an allowlist and anything unknown falls back to the default, so a sort column, filter or page size never
/// reaches the query unvalidated.
/// </summary>
public sealed record TenantListQuery
{
    public const int DefaultPageSize = 25;
    public const int MaxSearchLength = 100;

    /// <summary>Plan filter value for tenants without a membership row.</summary>
    public const string NoPlan = "none";

    public static IReadOnlyList<int> AllowedPageSizes { get; } = [10, 25, 50, 100];

    /// <summary>Browser tokens for the sortable columns. Nothing else is accepted.</summary>
    public static IReadOnlyDictionary<string, TenantListSortField> SortTokens { get; } =
        new Dictionary<string, TenantListSortField>(StringComparer.OrdinalIgnoreCase)
        {
            ["created"] = TenantListSortField.CreatedAt,
            ["name"] = TenantListSortField.Name,
            ["slug"] = TenantListSortField.Slug,
            ["updated"] = TenantListSortField.UpdatedAt,
            ["status"] = TenantListSortField.Status
        };

    public static IReadOnlyDictionary<string, TenantListStatusFilter> StatusTokens { get; } =
        new Dictionary<string, TenantListStatusFilter>(StringComparer.OrdinalIgnoreCase)
        {
            ["active"] = TenantListStatusFilter.Active,
            ["inactive"] = TenantListStatusFilter.Inactive
        };

    public static IReadOnlyDictionary<string, TenantMigrationRecordFilter> MigrationTokens { get; } =
        new Dictionary<string, TenantMigrationRecordFilter>(StringComparer.OrdinalIgnoreCase)
        {
            ["succeeded"] = TenantMigrationRecordFilter.Succeeded,
            ["failed"] = TenantMigrationRecordFilter.Failed,
            ["not-recorded"] = TenantMigrationRecordFilter.NotRecorded
        };

    public static TenantListQuery Default { get; } = new();

    public string? Search { get; init; }
    public TenantListStatusFilter Status { get; init; }
    public TenantMigrationRecordFilter Migration { get; init; }

    /// <summary>A plan code from the plan catalog, <see cref="NoPlan"/>, or null for all plans.</summary>
    public string? Plan { get; init; }

    public TenantListSortField Sort { get; init; } = TenantListSortField.CreatedAt;
    public TenantListSortDirection Direction { get; init; } = TenantListSortDirection.Descending;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;

    public bool HasFilters =>
        Search is not null || Status != TenantListStatusFilter.All || Migration != TenantMigrationRecordFilter.All || Plan is not null;

    public static TenantListQuery Create(
        string? search,
        string? status,
        string? migration,
        string? plan,
        string? sort,
        string? direction,
        int? page,
        int? pageSize,
        IEnumerable<string> knownPlanCodes)
    {
        var sortField = sort is not null && SortTokens.TryGetValue(sort.Trim(), out var parsedSort)
            ? parsedSort
            : TenantListSortField.CreatedAt;

        return new TenantListQuery
        {
            Search = NormalizeSearch(search),
            Status = status is not null && StatusTokens.TryGetValue(status.Trim(), out var parsedStatus)
                ? parsedStatus
                : TenantListStatusFilter.All,
            Migration = migration is not null && MigrationTokens.TryGetValue(migration.Trim(), out var parsedMigration)
                ? parsedMigration
                : TenantMigrationRecordFilter.All,
            Plan = NormalizePlan(plan, knownPlanCodes),
            Sort = sortField,
            Direction = ParseDirection(direction) ?? DefaultDirection(sortField),
            Page = page is > 0 ? page.Value : 1,
            PageSize = pageSize is { } size && AllowedPageSizes.Contains(size) ? size : DefaultPageSize
        };
    }

    /// <summary>Names and slugs read naturally A→Z; dates and status read newest/active first.</summary>
    public static TenantListSortDirection DefaultDirection(TenantListSortField field) =>
        field is TenantListSortField.Name or TenantListSortField.Slug
            ? TenantListSortDirection.Ascending
            : TenantListSortDirection.Descending;

    public static string SortToken(TenantListSortField field) =>
        SortTokens.First(pair => pair.Value == field).Key;

    public static string? StatusToken(TenantListStatusFilter status) =>
        StatusTokens.FirstOrDefault(pair => pair.Value == status).Key;

    public static string? MigrationToken(TenantMigrationRecordFilter migration) =>
        MigrationTokens.FirstOrDefault(pair => pair.Value == migration).Key;

    public static string DirectionToken(TenantListSortDirection direction) =>
        direction == TenantListSortDirection.Ascending ? "asc" : "desc";

    private static TenantListSortDirection? ParseDirection(string? direction) =>
        direction?.Trim().ToLowerInvariant() switch
        {
            "asc" => TenantListSortDirection.Ascending,
            "desc" => TenantListSortDirection.Descending,
            _ => null
        };

    private static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        var cleaned = new string(search.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxSearchLength)
            cleaned = cleaned[..MaxSearchLength].Trim();

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NormalizePlan(string? plan, IEnumerable<string> knownPlanCodes)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return null;

        var trimmed = plan.Trim();
        if (string.Equals(trimmed, NoPlan, StringComparison.OrdinalIgnoreCase))
            return NoPlan;

        return knownPlanCodes.FirstOrDefault(code => string.Equals(code, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The CentralDb outcome of the last tenant-database migration the CLI or provisioning recorded.</summary>
public enum TenantMigrationRecord
{
    NotRecorded = 0,
    Succeeded = 1,
    Failed = 2,
    Unknown = 3
}

public sealed record TenantListItem(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryDomain,
    bool IsActive,
    string? PlanCode,
    MembershipStatus? MembershipStatus,
    TenantMigrationRecord MigrationRecord,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int ActivePrintBridgeDevices,
    int OnlinePrintBridgeDevices);

/// <param name="Query">The query actually applied, with the page clamped to the last page.</param>
/// <param name="RegistryIsEmpty">True only when no tenant exists at all (as opposed to no tenant matching the filters).</param>
public sealed record TenantListPage(
    TenantListQuery Query,
    IReadOnlyList<TenantListItem> Items,
    int TotalCount,
    bool RegistryIsEmpty)
{
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)Query.PageSize);
}

// Tenant detail (CentralDb) ----------------------------------------------------------------------

public sealed record TenantOperationsDetail(
    Guid Id,
    string Name,
    string Slug,
    string PrimaryDomain,
    string DatabaseName,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool DatabaseConfigured,
    TenantMigrationRecord MigrationRecord,
    DateTime? LastMigrationAtUtc,
    TenantMembershipSummary? Membership,
    TenantRegistrationSummary? Registration,
    IReadOnlyList<TenantPrintBridgeDeviceSummary> PrintBridgeDevices,
    DateTime ReadAtUtc);

public sealed record TenantMembershipSummary(
    string PlanCode,
    MembershipStatus Status,
    string BillingPeriod,
    DateTime StartedAtUtc,
    DateTime? TrialEndsAtUtc,
    DateTime? CurrentPeriodEndsAtUtc);

public sealed record TenantRegistrationSummary(
    Guid Id,
    PendingRegistrationStatus Status,
    DateTime CreatedAtUtc,
    DateTime? PaymentSucceededAtUtc,
    DateTime? PaymentFailedAtUtc,
    DateTime? ProvisionedAtUtc);

/// <summary>Safe device metadata only: no token, token hash, installation id or IP address.</summary>
public sealed record TenantPrintBridgeDeviceSummary(
    string Name,
    bool IsActive,
    DateTime? LastSeenAtUtc,
    string? AppVersion,
    PrintBridgeConnectionStatus ConnectionStatus);

// Tenant detail (tenant database) ----------------------------------------------------------------

public enum TenantDatabaseState
{
    /// <summary>The database answered; the sections below were read.</summary>
    Reachable = 0,

    /// <summary>The central record has no stored connection details.</summary>
    NotConfigured = 1,

    /// <summary>The database server or database could not be reached.</summary>
    Unreachable = 2,

    /// <summary>The read did not finish within the health-read timeout.</summary>
    TimedOut = 3,

    /// <summary>The stored connection details could not be decrypted or parsed.</summary>
    ConfigurationUnreadable = 4,

    /// <summary>An unexpected failure; nothing about the database is known.</summary>
    Failed = 5
}

public enum TenantMigrationState
{
    Current = 0,
    Pending = 1,

    /// <summary>The database has migrations this application build does not know (it is newer than the app).</summary>
    DatabaseAhead = 2,
    Unknown = 3
}

public sealed record TenantMigrationStatus(
    TenantMigrationState State,
    int AppliedCount,
    int ExpectedCount,
    int PendingCount,
    string? LatestAppliedMigration,
    string? LatestExpectedMigration);

/// <summary>How the order clients of this process are wired, as registered in DI.</summary>
public enum ProviderClientKind
{
    Mock = 0,
    Real = 1,
    NotRegistered = 2
}

public enum ProviderConnectionHealthState
{
    Healthy = 0,
    Disabled = 1,

    /// <summary>The tenant's Order Sync setting is off; the Worker skips the tenant.</summary>
    SyncOff = 2,

    /// <summary>Repeated failures opened the circuit breaker; the Worker skips the connection until it closes.</summary>
    CircuitOpen = 3,
    Failing = 4,
    NeverSynced = 5,
    Stale = 6
}

/// <summary>Safe connection metadata only: no API key, secret, supplier id or executor e-mail.</summary>
public sealed record TenantPlatformConnectionHealth(
    FoodPlatform Platform,
    string? StoreId,
    bool IsActive,
    ProviderClientKind ClientKind,
    DateTime? LastSyncAttemptUtc,
    DateTime? LastSuccessfulSyncUtc,
    DateTime? LastFailedSyncUtc,
    int ConsecutiveFailures,
    DateTime? CircuitOpenUntilUtc,
    ProviderConnectionHealthState State);

public sealed record TenantGuidedSetupSummary(int InProgress, int Completed, int Skipped);

public sealed record TenantUserSummary(int ActiveUsers, int ActiveOwners);

public sealed record TenantOrderActivity(
    int ReceivedLast24Hours,
    int ReceivedLast7Days,
    int OpenOrders,
    DateTime? LatestReceivedAtUtc);

public sealed record TenantPrintJobSummary(int Queued, int FailedLast24Hours);

public enum TenantProviderMode
{
    Unknown = 0,
    Mock = 1,
    Real = 2
}

/// <summary>
/// One tenant database's operational state. A null section could not be read (see <see cref="IsComplete"/>);
/// when <see cref="Database"/> is not <see cref="TenantDatabaseState.Reachable"/> every section is null.
/// </summary>
public sealed record TenantOperationalHealth(
    TenantDatabaseState Database,
    TenantMigrationStatus? Migrations,
    TenantAutomationStatus? Automation,
    TenantGuidedSetupSummary? GuidedSetup,
    TenantUserSummary? Users,
    TenantProviderMode ProviderMode,
    IReadOnlyList<TenantPlatformConnectionHealth>? Connections,
    TenantOrderActivity? Orders,
    TenantPrintJobSummary? PrintJobs,
    DateTime ReadAtUtc)
{
    public bool IsReachable => Database == TenantDatabaseState.Reachable;

    public bool IsComplete =>
        IsReachable
        && Migrations is not null
        && Automation is not null
        && GuidedSetup is not null
        && Users is not null
        && Connections is not null
        && Orders is not null
        && PrintJobs is not null;

    public static TenantOperationalHealth Unavailable(TenantDatabaseState state, TenantProviderMode providerMode, DateTime readAtUtc) =>
        new(state, null, null, null, null, providerMode, null, null, null, readAtUtc);
}
