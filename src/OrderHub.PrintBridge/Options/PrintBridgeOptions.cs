namespace OrderHub.PrintBridge.Options;

public sealed class PrintBridgeOptions
{
    public const string SectionName = "PrintBridge";

    public string PrinterMode { get; set; } = "WindowsPrinter";

    public string PrinterName { get; set; } = string.Empty;

    public int IdlePollIntervalSeconds { get; set; } = 5;

    public int BusyPollIntervalSeconds { get; set; } = 1;

    public int ErrorPollIntervalSeconds { get; set; } = 15;

    public int MaxJobsPerPoll { get; set; } = 3;

    public bool DryRun { get; set; } = true;

    public string BridgeName { get; set; } = string.Empty;
}
