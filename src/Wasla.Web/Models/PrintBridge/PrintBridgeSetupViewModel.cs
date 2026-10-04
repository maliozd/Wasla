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

    /// <summary>Whether the user passes the device pages' policy; links to those pages are rendered only then.</summary>
    public bool CanManageDevices { get; set; }

    /// <summary>Set only while Print Bridge is the user's current guided-setup section.</summary>
    public Wasla.Web.Models.GuidedSetup.GuidedSetupSectionPanelViewModel? GuidedSetup { get; set; }

    /// <summary>
    /// The guided first install: set by the server only while the user's guided setup is in progress at the Print
    /// Bridge section (which needs both Print Bridge policies). The page then offers one linear path for a new device
    /// (download, install, open and connect) instead of the setup-type choice and reconnect.
    /// </summary>
    public bool IsGuidedFirstInstall { get; set; }
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
