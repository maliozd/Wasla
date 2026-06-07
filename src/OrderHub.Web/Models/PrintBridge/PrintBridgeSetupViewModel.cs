namespace OrderHub.Web.Models.PrintBridge;

public sealed class PrintBridgeSetupViewModel
{
    public string PackageDownloadUrl { get; set; } = "/print-bridge/download/package";
    public string DevicesUrl { get; set; } = "/print-bridge";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ExampleConfigJson { get; set; } = string.Empty;
    public string PowerShellPrinterCommand { get; set; } =
        "Get-Printer | Select-Object Name, DriverName, PortName, PrinterStatus";

    public bool AutoApproveNewOrders { get; set; }
    public bool AutoPrintReceiptOnAutoApprove { get; set; }
    public int ReceiptPrintCopyCount { get; set; } = 1;

    public int AllowedActiveDeviceCount { get; set; } = 3;
    public int ActiveDeviceCount { get; set; }
    public bool CanCreateActiveDevice { get; set; } = true;
    public bool ActiveCountExceedsLimit { get; set; }
}
