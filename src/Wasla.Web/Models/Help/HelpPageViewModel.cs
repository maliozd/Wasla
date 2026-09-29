namespace Wasla.Web.Models.Help;

public sealed class HelpPageViewModel
{
    public bool CanOpenPlatformConnections { get; init; }

    public bool CanOpenPrintBridgeSetup { get; init; }

    public bool CanOpenPrintBridgeDevices { get; init; }

    public bool CanOpenReceiptPrinterSettings { get; init; }

    public bool CanOpenLiveScreen { get; init; }

    public bool CanOpenOrders { get; init; }

    /// <summary>From <c>LiveScreenVisibility.RecentDeliveredWindow</c>; never a second hardcoded value.</summary>
    public int DeliveredWindowMinutes { get; init; }
}
