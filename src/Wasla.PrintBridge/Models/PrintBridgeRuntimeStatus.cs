namespace Wasla.PrintBridge.Models;

public sealed class PrintBridgeRuntimeStatus
{
    public bool IsRunning { get; init; }
    public bool IsConnected { get; init; }
    public DateTime? LastSuccessfulContactUtc { get; init; }
    public DateTime? LastPollUtc { get; init; }
    public string? LastError { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string PrinterName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool ServerDeviceNameResolved { get; init; }
    public string MachineName { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public IReadOnlyList<LocalPrintJobRecord> RecentJobs { get; init; } = Array.Empty<LocalPrintJobRecord>();
    public int JobsTodayCount { get; init; }
    public int FailedTodayCount { get; init; }
    public DateTime? LastPrintTimeUtc { get; init; }
    public BridgeServerConnectionStatus ServerConnectionStatus { get; init; }
    public PrinterHealthStatus PrinterHealthStatus { get; init; }
    public TrayIconState TrayIconState { get; init; }

    public static BridgeServerConnectionStatus ResolveServerConnectionStatus(
        bool isRunning,
        bool isConnected,
        string? lastError)
    {
        if (!isRunning)
            return BridgeServerConnectionStatus.Stopped;

        if (!string.IsNullOrWhiteSpace(lastError))
            return BridgeServerConnectionStatus.Error;

        return isConnected
            ? BridgeServerConnectionStatus.Connected
            : BridgeServerConnectionStatus.Disconnected;
    }

    public static TrayIconState ResolveTrayIconState(
        bool isRunning,
        bool isConnected,
        IReadOnlyList<LocalPrintJobRecord> jobs)
    {
        if (jobs.Any(j => j.Status == LocalPrintJobStatus.Printing))
            return TrayIconState.Printing;

        if (isRunning && !isConnected)
            return TrayIconState.ConnectionLost;

        if (isRunning)
            return TrayIconState.Polling;

        if (isConnected)
            return TrayIconState.Connected;

        return TrayIconState.ConnectionLost;
    }
}
