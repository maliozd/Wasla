using Wasla.Application.Abstractions.Printing;

namespace Wasla.Web.Models.PrintBridge;

public sealed class PrintBridgePageViewModel
{
    public string SetupUrl { get; set; } = "/print-bridge/setup";
    public string DevicesUrl { get; set; } = "/print-bridge/devices";

    public IReadOnlyList<PrintBridgeDeviceRowViewModel> Devices { get; set; } =
        Array.Empty<PrintBridgeDeviceRowViewModel>();

    public int AllowedActiveDeviceCount { get; set; } = 3;
    public int ActiveDeviceCount { get; set; }
    public bool CanCreateActiveDevice { get; set; } = true;
    public bool ActiveCountExceedsLimit { get; set; }

    public int PrintJobsTodayCount { get; set; }

    public string? LastConnectedDeviceName { get; set; }

    public DateTime? LastConnectedAtUtc { get; set; }

    public string ReceiptPrinterSettingsUrl { get; set; } = "/settings/receipt-printer";

    public string PackageDownloadUrl { get; set; } = "/print-bridge/download/package";

    /// <summary>Customer web base URL for Print Bridge desktop Server URL field (trailing slash).</summary>
    public string? ServerUrl { get; set; }

    public IReadOnlyList<PrintJobHistoryRowViewModel> PrintJobs { get; set; } =
        Array.Empty<PrintJobHistoryRowViewModel>();
}

public sealed class PrintJobHistoryRowViewModel
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string OrderDisplay { get; set; } = string.Empty;
    public string? ExternalOrderId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string PlatformDisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusLabelKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? PrintedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LockedBy { get; set; }
    public string? OrderCustomerName { get; set; }
    public decimal? TotalAmount { get; set; }
    public bool CanReprint { get; set; }
}

public sealed class PrintBridgeDeviceRowViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public PrintBridgeConnectionStatus ConnectionStatus { get; set; }
    public string ConnectionStatusLabelKey { get; set; } = string.Empty;
    public DateTime? LastSeenAtUtc { get; set; }
    public string? MachineName { get; set; }
    public string? PrinterName { get; set; }
    public string? AppVersion { get; set; }

    public bool IsConnected => ConnectionStatus == PrintBridgeConnectionStatus.Connected;
}
