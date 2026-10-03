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

    /// <summary>
    /// Temporary Development tool: the current tenant's slug, which the Owner must type to confirm the reset.
    /// Null (and the section is not rendered at all) unless the environment is Development, the option is
    /// enabled and the user is the tenant's Owner.
    /// </summary>
    public string? DevelopmentTenantResetSlug { get; init; }

    public bool ShowDevelopmentTools => DevelopmentTenantResetSlug is not null;
}
