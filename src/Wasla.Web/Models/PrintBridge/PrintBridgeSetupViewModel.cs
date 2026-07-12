namespace Wasla.Web.Models.PrintBridge;

public sealed class PrintBridgeSetupViewModel
{
    public const string DefaultPackageFileName = "Wasla.PrintBridge-win-x64.zip";

    public string PackageDownloadUrl { get; set; } = "/print-bridge/download/package";
    public string DevicesUrl { get; set; } = "/print-bridge/devices";
    public string? ServerUrl { get; set; }
    public bool PackageAvailable { get; set; }
    public string PackageFileName { get; set; } = DefaultPackageFileName;
    public bool HasActiveDevice { get; set; }
    public Guid? ActiveDeviceId { get; set; }
    public string? ActiveDeviceName { get; set; }
    public List<PrintBridgeSetupDeviceOptionViewModel> Devices { get; set; } = [];
    public bool IsDevelopment { get; set; }
}

public sealed class PrintBridgeSetupDeviceOptionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public bool IsActive { get; set; }
    public string ConnectionStatusLabelKey { get; set; } = string.Empty;
    public DateTime? LastSeenAtUtc { get; set; }
}
