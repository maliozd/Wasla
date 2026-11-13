namespace Wasla.PrintBridge.Options;

public sealed class PrintBridgeOptions
{
    public const string SectionName = "PrintBridge";

    public string PrinterMode { get; set; } = "WindowsPrinter";

    public string PrinterName { get; set; } = string.Empty;

    public int IdlePollIntervalSeconds { get; set; } = 5;

    public int BusyPollIntervalSeconds { get; set; } = 1;

    public int ErrorPollIntervalSeconds { get; set; } = 15;

    public int MaxJobsPerPoll { get; set; } = 3;

    public bool DryRun { get; set; } = true;

    /// <summary>Stable local installation identity. Generated once and sent to Wasla for device binding.</summary>
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>User-facing device label (e.g. Kasadaki POS). Synced from the Wasla Web panel.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether <see cref="DisplayName"/> was resolved from the server for the current token.</summary>
    public bool ServerDeviceNameResolved { get; set; }

    /// <summary>Windows machine name for diagnostics and server last-seen tracking.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// Set by an explicit Stop in the app and cleared by any start (WAS-61): the service does not start printing on its
    /// own while it is set, so a service restart, a crash recovery or a reboot never undoes the user's Stop. Closing the
    /// app does not set it.
    /// </summary>
    public bool PrintingStoppedByUser { get; set; }

    /// <summary>Legacy setting migrated to <see cref="DisplayName"/> on load; not used as a separate alias.</summary>
    public string BridgeName { get; set; } = string.Empty;

    /// <summary>
    /// A separate copy to change and then publish with <see cref="Services.PrintBridgeSettingsHolder.Replace"/>,
    /// so code that captured the current object never sees a half-applied change. Every property is a value.
    /// </summary>
    public PrintBridgeOptions Clone() => (PrintBridgeOptions)MemberwiseClone();
}
