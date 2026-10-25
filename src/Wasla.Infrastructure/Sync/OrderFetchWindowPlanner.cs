using Wasla.Application.Platform.Dtos;

namespace Wasla.Infrastructure.Sync;

/// <summary>
/// Plans the provider modification-time windows of a connection from its checkpoint
/// (<c>PlatformConnection.LastSuccessfulSync</c>: everything modified before it has been fetched and persisted).
/// </summary>
/// <remarks>
/// <para>
/// The current pass (<see cref="PlanCurrent"/>) always ends at now. When the interval since the checkpoint fits one
/// window, that window covers it and moves the checkpoint to now. When it does not (a history gap after an outage),
/// the current pass fetches only the hot window <c>[now − window, now]</c> so new orders keep arriving, and leaves the
/// checkpoint where it is.
/// </para>
/// <para>
/// Backfill (<see cref="PlanBackfill"/>) then closes the gap oldest first, one window at a time, each starting
/// <see cref="CheckpointOverlap"/> before the checkpoint. The checkpoint only ever moves to the end of a window that
/// was fully fetched and persisted, so it never jumps over the gap; backfill runs up to now, and the part it shares
/// with the hot window is absorbed by idempotent upserts.
/// </para>
/// </remarks>
internal static class OrderFetchWindowPlanner
{
    /// <summary>Re-read before the checkpoint on every window, for clock skew, provider indexing lag and the boundary instant.</summary>
    internal static readonly TimeSpan CheckpointOverlap = TimeSpan.FromMinutes(5);

    /// <summary>A connection that has never completed a sync starts this far back: the window it always used.</summary>
    internal static readonly TimeSpan InitialLookback = TimeSpan.FromHours(1);

    /// <summary>
    /// Recovery workload cap: backfill windows one connection may fetch in one Worker cycle. It bounds how long one
    /// connection's recovery runs per cycle. It is not rate-limit protection: each window can take up to 20 page
    /// requests, and the request rate is governed by the process-wide request limiter.
    /// </summary>
    internal const int MaxRecoveryWindowsPerCycle = 12;

    /// <summary>The window of the current pass: everything since the checkpoint when it fits, otherwise the hot window.</summary>
    internal static CurrentWindowPlan PlanCurrent(DateTime? checkpointUtc, DateTime nowUtc, TimeSpan? maxWindow)
    {
        nowUtc = AsUtc(nowUtc);
        var startUtc = checkpointUtc is { } checkpoint
            ? Min(AsUtc(checkpoint), nowUtc) - CheckpointOverlap
            : nowUtc - InitialLookback;

        if (maxWindow is { } length && length > TimeSpan.Zero && nowUtc - startUtc > length)
            return new CurrentWindowPlan(new OrderFetchWindow(nowUtc - length, nowUtc), AdvancesCheckpoint: false);

        return new CurrentWindowPlan(new OrderFetchWindow(startUtc, nowUtc), AdvancesCheckpoint: true);
    }

    /// <summary>
    /// The next backfill windows, oldest first, from <see cref="CheckpointOverlap"/> before the checkpoint up to now.
    /// Each window is at most <paramref name="maxWindow"/> long and starts at the previous one's end.
    /// </summary>
    internal static IReadOnlyList<OrderFetchWindow> PlanBackfill(
        DateTime checkpointUtc,
        DateTime nowUtc,
        TimeSpan maxWindow,
        int maxWindows)
    {
        nowUtc = AsUtc(nowUtc);
        var windows = new List<OrderFetchWindow>();
        var windowStart = Min(AsUtc(checkpointUtc), nowUtc) - CheckpointOverlap;
        while (windowStart < nowUtc && windows.Count < maxWindows)
        {
            var windowEnd = Min(windowStart + maxWindow, nowUtc);
            windows.Add(new OrderFetchWindow(windowStart, windowEnd));
            windowStart = windowEnd;
        }

        return windows;
    }

    /// <summary>EF reads datetime2 back as Unspecified; Wasla stores UTC.</summary>
    internal static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
}

/// <param name="Window">The window to fetch; it always ends at now.</param>
/// <param name="AdvancesCheckpoint">
/// False for a hot window after a history gap: it is fetched first for freshness, but the checkpoint stays until
/// backfill has covered the gap.
/// </param>
internal sealed record CurrentWindowPlan(OrderFetchWindow Window, bool AdvancesCheckpoint);
