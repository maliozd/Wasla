namespace OrderHub.Web.Models.PrintBridge;

public sealed class PrintBridgeSetupViewModel
{
    public string PackageDownloadUrl { get; set; } = "/print-bridge/download/package";
    public string DevicesUrl { get; set; } = "/print-bridge/devices";
    public string? ServerUrl { get; set; }

    public int AllowedActiveDeviceCount { get; set; } = 3;
    public int ActiveDeviceCount { get; set; }
    public bool CanCreateActiveDevice { get; set; } = true;
    public bool ActiveCountExceedsLimit { get; set; }
}
