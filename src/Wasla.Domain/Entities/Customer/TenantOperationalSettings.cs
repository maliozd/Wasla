using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// Per-tenant operational settings stored in the CustomerDb (singleton row).
/// When the row does not exist, services return safe defaults (sync enabled; auto-approve off).
/// </summary>
public sealed class TenantOperationalSettings : BaseEntity
{
    public bool OrderSyncEnabled { get; set; } = true;

    public bool AutoApproveNewOrders { get; set; }

    public bool AutoPrintReceiptOnAutoApprove { get; set; }

    public int ReceiptPrintCopyCount { get; set; } = 1;

    /// <summary>
    /// JSON-serialized receipt template/content settings.
    /// </summary>
    public string? ReceiptTemplateSettingsJson { get; set; }

    /// <summary>
    /// When set, the tenant has dismissed the initial setup guidance.
    /// This is not operational readiness and is not cleared when a platform later disconnects.
    /// </summary>
    public DateTime? SetupGuidanceCompletedAtUtc { get; set; }

    /// <summary>
    /// Setup until the first user completes or skips guided setup, then Live. A missing row is Live, and so is a row
    /// created lazily for an existing tenant; only provisioning (and the Development tenant reset) writes Setup.
    /// </summary>
    public TenantOperationalMode OperationalMode { get; set; } = TenantOperationalMode.Live;
}

