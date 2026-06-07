namespace OrderHub.Application.Abstractions.Printing;

public enum PrintBridgeConnectionStatus
{
    Inactive,
    NeverConnected,
    Connected,
    RecentlySeen,
    Disconnected
}

public static class PrintBridgeConnectionStatusCalculator
{
    public static readonly TimeSpan ConnectedThreshold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RecentlySeenThreshold = TimeSpan.FromMinutes(5);

    public static PrintBridgeConnectionStatus Calculate(
        bool isActive,
        DateTime? lastSeenAtUtc,
        DateTime? utcNow = null)
    {
        if (!isActive)
            return PrintBridgeConnectionStatus.Inactive;

        if (!lastSeenAtUtc.HasValue)
            return PrintBridgeConnectionStatus.NeverConnected;

        var now = utcNow ?? DateTime.UtcNow;
        var age = now - lastSeenAtUtc.Value;

        if (age <= ConnectedThreshold)
            return PrintBridgeConnectionStatus.Connected;

        if (age <= RecentlySeenThreshold)
            return PrintBridgeConnectionStatus.RecentlySeen;

        return PrintBridgeConnectionStatus.Disconnected;
    }

    public static string GetLabelKey(PrintBridgeConnectionStatus status) =>
        status switch
        {
            PrintBridgeConnectionStatus.Connected => "PrintBridge.StatusConnected",
            PrintBridgeConnectionStatus.RecentlySeen => "PrintBridge.StatusRecentlySeen",
            PrintBridgeConnectionStatus.Disconnected => "PrintBridge.StatusDisconnected",
            PrintBridgeConnectionStatus.NeverConnected => "PrintBridge.StatusNeverConnected",
            PrintBridgeConnectionStatus.Inactive => "PrintBridge.StatusInactive",
            _ => "PrintBridge.StatusInactive"
        };
}
