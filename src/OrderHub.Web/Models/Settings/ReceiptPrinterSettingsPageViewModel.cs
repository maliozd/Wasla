namespace OrderHub.Web.Models.Settings;

public sealed class ReceiptPrinterSettingsPageViewModel
{
    public string CustomerDisplayName { get; set; } = string.Empty;

    public PrintBridgeStatusSummaryViewModel? PrintBridgeStatus { get; set; }
}

public sealed class PrintBridgeStatusSummaryViewModel
{
    public bool HasDevices { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public string ConnectionStatusLabelKey { get; set; } = string.Empty;

    public DateTime? LastSeenAtUtc { get; set; }
}
