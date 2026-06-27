namespace Wasla.PrintBridge.Models;

public sealed class PrintBridgeRuntimeStatus
{
    public bool IsRunning { get; init; }
    public bool IsConnected { get; init; }
    public DateTime? LastSuccessfulContactUtc { get; init; }
    public DateTime? LastPollUtc { get; init; }
    public PrintBridgeRuntimeIssue? LastIssue { get; init; }
    public string? LastError { get; init; }
    public string ServerUrl { get; init; } = string.Empty;
    public string PrinterName { get; init; } = string.Empty;
    public string LocalDeviceName { get; init; } = string.Empty;
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
        bool isConfigured,
        bool isRunning,
        bool isConnected,
        PrintBridgeRuntimeIssue? lastIssue)
    {
        if (!isConfigured)
            return BridgeServerConnectionStatus.NotConfigured;

        if (lastIssue is not null)
            return BridgeServerConnectionStatus.Error;

        if (!isRunning)
            return BridgeServerConnectionStatus.Stopped;

        return isConnected
            ? BridgeServerConnectionStatus.Connected
            : BridgeServerConnectionStatus.Disconnected;
    }

    public static BridgeServerConnectionStatus ResolveServerConnectionStatus(
        bool isConfigured,
        bool isRunning,
        bool isConnected,
        string? lastError) =>
        ResolveServerConnectionStatus(
            isConfigured,
            isRunning,
            isConnected,
            string.IsNullOrWhiteSpace(lastError)
                ? null
                : PrintBridgeRuntimeIssue.FromResource(lastError));

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

    public static string ResolveLocalDeviceLabel(string? localDeviceName, string? machineName)
    {
        if (!string.IsNullOrWhiteSpace(localDeviceName))
            return localDeviceName.Trim();

        if (!string.IsNullOrWhiteSpace(machineName))
            return machineName.Trim();

        return Environment.MachineName;
    }
}
