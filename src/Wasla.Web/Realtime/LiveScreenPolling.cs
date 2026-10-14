using Wasla.Infrastructure.Realtime;

namespace Wasla.Web.Realtime;

/// <summary>
/// The Live Screen's snapshot polling rates, rendered into the page (WAS-24, WAS-103). A connected screen polls less
/// only when this Wasla.Web receives Wasla.Worker and Wasla.Api changes over its internal listener. Without the
/// listener new platform orders and automatic approvals reach a screen only by polling, however healthy its own live
/// connection is, so it keeps the normal rate.
/// </summary>
public sealed class LiveScreenPolling
{
    /// <summary>The normal rate, kept while not connected.</summary>
    public const int NormalIntervalMs = 10000;

    /// <summary>The lower safety rate while connected, when notifications carry every process's changes.</summary>
    public const int ConnectedWithInternalListenerIntervalMs = 30000;

    private LiveScreenPolling(int connectedIntervalMs) => ConnectedIntervalMs = connectedIntervalMs;

    public int IntervalMs => NormalIntervalMs;

    public int ConnectedIntervalMs { get; }

    public static LiveScreenPolling For(InternalRealtimeOptions internalOptions) =>
        new(internalOptions.IsEnabled ? ConnectedWithInternalListenerIntervalMs : NormalIntervalMs);
}
