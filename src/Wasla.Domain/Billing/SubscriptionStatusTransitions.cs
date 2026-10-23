using Wasla.Domain.Enums;

namespace Wasla.Domain.Billing;

/// <summary>
/// The states a <see cref="Wasla.Domain.Entities.Central.BillingSubscription"/> may hold and the moves between them.
/// A move that is not listed is refused, so a late, duplicate or reordered provider observation can never take a
/// subscription back to an earlier state (for example <c>Expired</c> to <c>Active</c>).
/// </summary>
/// <remarks>
/// <code>
/// Pending                -> Active, Cancelled, Expired, ReconciliationRequired
/// Active                 -> PastDue, Cancelled, Expired, ReconciliationRequired
/// PastDue                -> Active, Cancelled, Expired, ReconciliationRequired
/// Cancelled              -> Expired
/// ReconciliationRequired -> Active, PastDue, Cancelled, Expired
/// Expired                -> (terminal)
/// </code>
/// Which of these states grant access to the tenant panel is entitlement policy (WAS-151), not part of this table.
/// </remarks>
public static class SubscriptionStatusTransitions
{
    private static readonly IReadOnlyDictionary<SubscriptionStatus, SubscriptionStatus[]> Allowed =
        new Dictionary<SubscriptionStatus, SubscriptionStatus[]>
        {
            [SubscriptionStatus.Pending] =
            [
                SubscriptionStatus.Active, SubscriptionStatus.Cancelled, SubscriptionStatus.Expired,
                SubscriptionStatus.ReconciliationRequired
            ],
            [SubscriptionStatus.Active] =
            [
                SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled, SubscriptionStatus.Expired,
                SubscriptionStatus.ReconciliationRequired
            ],
            [SubscriptionStatus.PastDue] =
            [
                SubscriptionStatus.Active, SubscriptionStatus.Cancelled, SubscriptionStatus.Expired,
                SubscriptionStatus.ReconciliationRequired
            ],
            [SubscriptionStatus.Cancelled] = [SubscriptionStatus.Expired],
            [SubscriptionStatus.ReconciliationRequired] =
            [
                SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled,
                SubscriptionStatus.Expired
            ],
            [SubscriptionStatus.Expired] = []
        };

    /// <summary>The states a billing subscription can hold; <c>None</c> and <c>Trialing</c> are not among them.</summary>
    public static IReadOnlyCollection<SubscriptionStatus> BillingStates { get; } = Allowed.Keys.ToArray();

    public static bool IsBillingState(SubscriptionStatus status) => Allowed.ContainsKey(status);

    public static bool IsAllowed(SubscriptionStatus from, SubscriptionStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>States that require a provider subscription reference: the provider has verified a subscription.</summary>
    public static bool RequiresProviderReference(SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;
}
