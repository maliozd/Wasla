using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Setup;

/// <summary>What an automation does right now, as opposed to how it is configured.</summary>
public enum AutomationState
{
    /// <summary>Configured off.</summary>
    Off = 0,

    /// <summary>Configured on and operating.</summary>
    Active = 1,

    /// <summary>
    /// Configured on, but the tenant is still in Setup: it starts by itself once guided setup is completed or skipped.
    /// </summary>
    PendingSetup = 2
}

/// <summary>
/// The tenant's operational mode with the saved automation settings it governs, read together from the tenant's settings
/// row. The configured flags are the saved settings and are never changed because of the mode; the effective states
/// say what actually happens now. This is the same rule the automation services apply: automatic approval and
/// automatic receipts run only when configured on and the tenant is Live. Order synchronization does not depend on
/// the mode: orders are fetched and kept in Setup too.
/// </summary>
public sealed record TenantAutomationStatus(
    TenantOperationalMode Mode,
    bool OrderSyncConfigured,
    bool AutoApproveConfigured,
    bool AutoReceiptConfigured)
{
    /// <summary>A tenant without a settings row: the established defaults (Live, sync on, no automatic approval or receipts).</summary>
    public static TenantAutomationStatus WithoutSettings { get; } = new(TenantOperationalMode.Live, true, false, false);

    public AutomationState OrderSync => OrderSyncConfigured ? AutomationState.Active : AutomationState.Off;

    public AutomationState AutoApprove => Effective(Mode, AutoApproveConfigured);

    public AutomationState AutoReceipt => Effective(Mode, AutoReceiptConfigured);

    /// <summary>The effective state of automatic approval or automatic receipts configured as <paramref name="configured"/>.</summary>
    public static AutomationState Effective(TenantOperationalMode mode, bool configured) =>
        !configured
            ? AutomationState.Off
            : mode == TenantOperationalMode.Setup ? AutomationState.PendingSetup : AutomationState.Active;
}
