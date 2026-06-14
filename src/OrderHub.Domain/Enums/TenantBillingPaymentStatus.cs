namespace OrderHub.Domain.Enums;

/// <summary>
/// Tenant/subscription billing payment state (not order payment).
/// </summary>
public enum TenantBillingPaymentStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Cancelled = 3,
    Refunded = 4
}
