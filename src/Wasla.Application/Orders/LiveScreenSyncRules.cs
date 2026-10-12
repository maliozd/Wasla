using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Abstractions.PlatformConnections;
using Wasla.Application.Admin;

namespace Wasla.Application.Orders;

/// <summary>
/// The Live Screen's connection indicator, from the tenant's platform connection records only: the Order Sync setting,
/// and each connection's <c>IsActive</c>, last successful sync (the Worker's checkpoint, moved only after a fetch window
/// was fetched and stored completely), consecutive failures and circuit breaker. A sync attempt
/// (<c>LastSyncAttempt</c>) is not evidence of success, and neither is the absence of a recorded failure.
/// </summary>
public static class LiveScreenSyncRules
{
    /// <summary>
    /// How many expected syncs in a row a connection may go without a successful one before the Live Screen stops
    /// calling it live. The first absorbs a slow pass (a long fetch, the provider request limiter, history recovery
    /// delaying the next Worker cycle); more than that means orders may be missing from the screen.
    /// </summary>
    public const int ExpectedSyncsBeforeStale = 3;

    /// <summary>
    /// The longest a connection may go without a successful sync and still count as live: <see cref="ExpectedSyncsBeforeStale"/>
    /// times the gap between two expected syncs, which is the connection's own interval plus one Worker cycle (a due
    /// connection waits for the next cycle). The default 30-second interval gives 3 × (30 + 15) s = 135 seconds. The
    /// Central Admin view keeps its longer threshold (<see cref="ProviderConnectionHealthRules.StaleAfter"/>).
    /// </summary>
    public static TimeSpan StaleAfter(int syncIntervalSeconds) =>
        TimeSpan.FromSeconds(ExpectedSyncsBeforeStale * ((long)Math.Max(syncIntervalSeconds, 0) + OrderSyncSchedule.WorkerCycleSeconds));

    /// <summary>
    /// The indicator for one tenant. No connection: NotConnected. Order Sync off, or no active connection: SyncDisabled.
    /// Otherwise Issue when any active connection is not healthy (one healthy platform never hides another's problem),
    /// and Live only when every active connection is. Deactivated connections are reported as SyncDisabled and do not
    /// affect the overall state of active ones.
    /// </summary>
    public static LiveScreenSyncStatus Evaluate(
        IReadOnlyCollection<PlatformConnectionResult> connections,
        bool orderSyncEnabled,
        DateTime nowUtc)
    {
        var platforms = connections
            .Select(connection => new LiveScreenPlatformSync(connection.Platform, PlatformState(connection, orderSyncEnabled, nowUtc)))
            .ToList();

        var active = connections.Where(connection => connection.IsActive).ToList();
        var state = connections.Count == 0
            ? LiveScreenSyncState.NotConnected
            : !orderSyncEnabled || active.Count == 0
                ? LiveScreenSyncState.SyncDisabled
                : platforms.Any(platform => platform.State == LiveScreenSyncState.Issue)
                    ? LiveScreenSyncState.Issue
                    : LiveScreenSyncState.Live;

        // Everything up to this instant has been synchronized from every active platform. Unknown when one has never
        // completed a sync.
        DateTime? lastSuccessfulSync = active.Count == 0 || active.Any(connection => connection.LastSuccessfulSyncUtc is null)
            ? null
            : DateTime.SpecifyKind(active.Min(connection => connection.LastSuccessfulSyncUtc!.Value), DateTimeKind.Utc);

        return new LiveScreenSyncStatus(state, lastSuccessfulSync, platforms);
    }

    private static LiveScreenSyncState PlatformState(PlatformConnectionResult connection, bool orderSyncEnabled, DateTime nowUtc) =>
        ProviderConnectionHealthRules.Classify(
                connection.IsActive,
                orderSyncEnabled,
                connection.LastSuccessfulSyncUtc,
                connection.ConsecutiveFailures,
                connection.CircuitOpenUntilUtc,
                nowUtc,
                StaleAfter(connection.SyncIntervalSeconds)) switch
            {
                ProviderConnectionHealthState.Healthy => LiveScreenSyncState.Live,
                ProviderConnectionHealthState.Disabled or ProviderConnectionHealthState.SyncOff => LiveScreenSyncState.SyncDisabled,
                _ => LiveScreenSyncState.Issue
            };
}
