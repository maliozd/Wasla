namespace Wasla.Domain.Enums;

/// <summary>
/// Subscription state of a tenant. <see cref="None"/> and <see cref="Trialing"/> are tenant-level values only; a
/// <see cref="Wasla.Domain.Entities.Central.BillingSubscription"/> never holds them (see
/// <see cref="Wasla.Domain.Billing.SubscriptionStatusTransitions"/>).
/// </summary>
public enum SubscriptionStatus
{
    None = 0,
    Trialing = 1,
    Active = 2,
    PastDue = 3,
    Cancelled = 4,
    Expired = 5,

    /// <summary>Checkout started; the provider has not verified a subscription yet. Grants no paid access.</summary>
    Pending = 6,

    /// <summary>
    /// The provider reported a state Wasla does not recognise, or the stored state could not be confirmed. Grants no
    /// paid access; only a newer verified provider state moves the subscription out of it.
    /// </summary>
    ReconciliationRequired = 7
}
