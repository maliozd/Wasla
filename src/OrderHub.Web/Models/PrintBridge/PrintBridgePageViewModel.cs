namespace OrderHub.Web.Models.PrintBridge;

public sealed class PrintBridgePageViewModel
{
    public string SetupUrl { get; set; } = "/print-bridge/download";

    public IReadOnlyList<PrintBridgeDeviceRowViewModel> Devices { get; set; } =
        Array.Empty<PrintBridgeDeviceRowViewModel>();

    public int AllowedActiveDeviceCount { get; set; } = 3;
    public int ActiveDeviceCount { get; set; }
    public bool CanCreateActiveDevice { get; set; } = true;
    public bool ActiveCountExceedsLimit { get; set; }
}

public sealed class PrintBridgeDeviceRowViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsConnected { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public string? MachineName { get; set; }
    public string? PrinterName { get; set; }
    public string? AppVersion { get; set; }
}
