namespace OrderHub.PrintBridge.Models;

public sealed class PrintBridgeRuntimeStatus
{
    public bool IsRunning { get; init; }
    public bool IsConnected { get; init; }
    public DateTime? LastSuccessfulContactUtc { get; init; }
    public DateTime? LastPollUtc { get; init; }
    public string? LastError { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public string PrinterName { get; init; } = string.Empty;
    public string BridgeName { get; init; } = string.Empty;
    public bool DryRun { get; init; }
    public IReadOnlyList<LocalPrintJobRecord> RecentJobs { get; init; } = Array.Empty<LocalPrintJobRecord>();
}
