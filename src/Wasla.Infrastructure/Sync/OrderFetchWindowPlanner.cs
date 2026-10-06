using Wasla.Application.Platform.Dtos;

namespace Wasla.Infrastructure.Sync;

/// <summary>
/// Plans the provider modification-time windows of one connection sync from its checkpoint
/// (<c>PlatformConnection.LastSuccessfulSync</c>: everything modified before it has been fetched and persisted).
/// </summary>
/// <remarks>
/// The interval starts <see cref="CheckpointOverlap"/> before the checkpoint, or <see cref="InitialLookback"/> before
/// now when there is none, and ends now. A client with a maximum window gets consecutive windows that share their
/// boundary instant, oldest first. At most <see cref="MaxWindowsPerRun"/> windows run at once; the sync advances the
/// checkpoint after each completed window, so the rest of a long outage continues on the next run instead of being
/// dropped.
/// </remarks>
internal static class OrderFetchWindowPlanner
{
    /// <summary>Re-read before the checkpoint on every run, for clock skew, provider indexing lag and the boundary instant.</summary>
    internal static readonly TimeSpan CheckpointOverlap = TimeSpan.FromMinutes(5);

    /// <summary>A connection that has never completed a sync starts this far back: the window it always used.</summary>
    internal static readonly TimeSpan InitialLookback = TimeSpan.FromHours(1);

    /// <summary>
    /// Windows one run fetches. With one-hour windows a run recovers up to twelve hours, and one connection's catch-up
    /// stays well below Trendyol GO's documented limit of 50 requests per endpoint in 10 seconds.
    /// </summary>
    internal const int MaxWindowsPerRun = 12;

    internal static OrderFetchPlan Plan(DateTime? checkpointUtc, DateTime nowUtc, TimeSpan? maxWindow)
    {
        nowUtc = AsUtc(nowUtc);
        var startUtc = checkpointUtc is { } checkpoint
            ? Min(AsUtc(checkpoint), nowUtc) - CheckpointOverlap
            : nowUtc - InitialLookback;

        if (maxWindow is not { } length || length <= TimeSpan.Zero)
            return new OrderFetchPlan([new OrderFetchWindow(startUtc, nowUtc)], HasMore: false);

        var windows = new List<OrderFetchWindow>();
        var windowStart = startUtc;
        while (windowStart < nowUtc && windows.Count < MaxWindowsPerRun)
        {
            var windowEnd = Min(windowStart + length, nowUtc);
            windows.Add(new OrderFetchWindow(windowStart, windowEnd));
            windowStart = windowEnd;
        }

        return new OrderFetchPlan(windows, HasMore: windowStart < nowUtc);
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

/// <param name="Windows">Consecutive windows to fetch, oldest first.</param>
/// <param name="HasMore">True when the interval up to now needs further runs.</param>
internal sealed record OrderFetchPlan(IReadOnlyList<OrderFetchWindow> Windows, bool HasMore);
