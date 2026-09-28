using Wasla.Domain.Common;

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
}

