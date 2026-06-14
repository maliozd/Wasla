namespace OrderHub.Web.Models.PrintBridge;

public sealed class PrintBridgeSetupViewModel
{
    public const string DefaultPackageFileName = "OrderHub.PrintBridge-win-x64.zip";

    public string PackageDownloadUrl { get; set; } = "/print-bridge/download/package";
    public string DevicesUrl { get; set; } = "/print-bridge/devices";
    public string? ServerUrl { get; set; }
    public bool PackageAvailable { get; set; }
    public string PackageFileName { get; set; } = DefaultPackageFileName;

    public int AllowedActiveDeviceCount { get; set; } = 3;
    public int ActiveDeviceCount { get; set; }
    public bool CanCreateActiveDevice { get; set; } = true;
    public bool ActiveCountExceedsLimit { get; set; }
}
