using Wasla.Domain.Billing;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Wasla's own record of one Wasla Orders subscription (WAS-147). It lives only in CentralDb and is the source of
/// truth for subscription identity and state; tenant databases hold no billing data.
/// </summary>
/// <remarks>
/// <para>
/// Owner: the <see cref="PendingRegistration"/> the checkout started from, always, and the <see cref="Tenant"/> that
/// registration was provisioned into once <see cref="LinkTenant"/> records it. One subscription per registration and
/// per tenant.
/// </para>
/// <para>
/// The plan, period, amount, currency, provider and environment are a snapshot taken when checkout started and never
/// change; a later plan catalog change does not reprice an existing subscription. Provider identifiers are opaque
/// references only. No card data, provider payload or secret is stored here.
/// </para>
/// <para>
/// <see cref="Revision"/> is the optimistic-concurrency token: every change raises it, and a save that read an older
/// revision fails instead of overwriting a concurrent change.
/// </para>
/// </remarks>
public sealed class BillingSubscription
{
    public const string MonthlyPeriod = "Monthly";
    public const string YearlyPeriod = "Yearly";
    public const int PlanCodeMaxLength = 50;
    public const int BillingPeriodMaxLength = 20;
    public const int ReferenceMaxLength = 100;

    private BillingSubscription()
    {
    }

    public Guid Id { get; private set; }

    public Guid PendingRegistrationId { get; private set; }

    public Guid? TenantId { get; private set; }

    public BillingProvider Provider { get; private set; }

    public BillingEnvironment Environment { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    /// <summary><see cref="MonthlyPeriod"/> or <see cref="YearlyPeriod"/>, as stored on the registration.</summary>
    public string BillingPeriod { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public SubscriptionStatus Status { get; private set; }

    /// <summary>
    /// When the provider state behind <see cref="Status"/> was verified (server-side provider read or authenticated
    /// provider event time, never a client value). A state observed earlier than this is stale and ignored.
    /// </summary>
    public DateTime StatusObservedAtUtc { get; private set; }

    public string? ExternalSubscriptionReference { get; private set; }

    public string? ExternalSubscriberReference { get; private set; }

    /// <summary>The latest verified paid period; it only ever moves forward.</summary>
    public DateTime? CurrentPeriodStartUtc { get; private set; }

    public DateTime? CurrentPeriodEndUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public int Revision { get; private set; }

    public BillingPrice Price => new(Amount, Currency);

    /// <summary>
    /// Fail-closed check: only an <see cref="SubscriptionStatus.Active"/> subscription with a verified paid period
    /// that has not ended. Pending, past due, cancelled, expired and reconciliation states are all false here; grace
    /// rules belong to the entitlement policy (WAS-151).
    /// </summary>
    public static bool IsVerifiedActive(SubscriptionStatus status, DateTime? currentPeriodEndUtc, DateTime nowUtc) =>
        status == SubscriptionStatus.Active && currentPeriodEndUtc is { } end && end > nowUtc;

    public bool IsVerifiedActive(DateTime nowUtc) => IsVerifiedActive(Status, CurrentPeriodEndUtc, nowUtc);

    public static BillingSubscription StartPending(
        Guid pendingRegistrationId,
        BillingProvider provider,
        BillingEnvironment environment,
        string planCode,
        string billingPeriod,
        BillingPrice price,
        DateTime nowUtc)
    {
        if (pendingRegistrationId == Guid.Empty)
            throw new ArgumentException("A subscription starts from a pending registration.", nameof(pendingRegistrationId));
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider));
        if (!Enum.IsDefined(environment))
            throw new ArgumentOutOfRangeException(nameof(environment));
        if (string.IsNullOrWhiteSpace(planCode) || planCode.Length > PlanCodeMaxLength || planCode != planCode.Trim())
            throw new ArgumentException("A plan code is required.", nameof(planCode));
        if (billingPeriod is not (MonthlyPeriod or YearlyPeriod))
            throw new ArgumentException("The billing period is Monthly or Yearly.", nameof(billingPeriod));
        if (price == default)
            throw new ArgumentException("A price is required.", nameof(price));

        return new BillingSubscription
        {
            Id = Guid.NewGuid(),
            PendingRegistrationId = pendingRegistrationId,
            Provider = provider,
            Environment = environment,
            PlanCode = planCode,
            BillingPeriod = billingPeriod,
            Amount = price.Amount,
            Currency = price.Currency,
            Status = SubscriptionStatus.Pending,
            StatusObservedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Revision = 1
        };
    }

    /// <summary>
    /// Records the provider's identifiers for this subscription. They are set once: repeating the same values is
    /// <see cref="BillingChangeResult.Unchanged"/>, different ones are a <see cref="BillingChangeResult.Conflict"/>.
    /// </summary>
    public BillingChangeResult BindProviderReferences(
        string externalSubscriptionReference,
        string? externalSubscriberReference,
        DateTime nowUtc)
    {
        ValidateReference(externalSubscriptionReference, nameof(externalSubscriptionReference));
        if (externalSubscriberReference is not null)
            ValidateReference(externalSubscriberReference, nameof(externalSubscriberReference));

        if (ExternalSubscriptionReference is not null
            && !string.Equals(ExternalSubscriptionReference, externalSubscriptionReference, StringComparison.Ordinal))
            return BillingChangeResult.Conflict;

        if (ExternalSubscriberReference is not null && externalSubscriberReference is not null
            && !string.Equals(ExternalSubscriberReference, externalSubscriberReference, StringComparison.Ordinal))
            return BillingChangeResult.Conflict;

        var changed = false;
        if (ExternalSubscriptionReference is null)
        {
            ExternalSubscriptionReference = externalSubscriptionReference;
            changed = true;
        }

        if (ExternalSubscriberReference is null && externalSubscriberReference is not null)
        {
            ExternalSubscriberReference = externalSubscriberReference;
            changed = true;
        }

        if (!changed)
            return BillingChangeResult.Unchanged;

        Touch(nowUtc);
        return BillingChangeResult.Applied;
    }

    /// <summary>
    /// Applies a verified provider state. An observation that is older than the stored one is
    /// <see cref="BillingChangeResult.Stale"/>; a move <see cref="SubscriptionStatusTransitions"/> does not list, or one
    /// to a state that needs a provider reference this subscription does not have, is
    /// <see cref="BillingChangeResult.InvalidTransition"/>. Neither changes anything.
    /// </summary>
    /// <remarks>
    /// The same state observed again at a later time only moves <see cref="StatusObservedAtUtc"/> forward, so an older
    /// observation that arrives afterwards is still recognised as stale. A different state observed at exactly the
    /// stored time is stale too: two observations of one instant cannot be ordered.
    /// </remarks>
    public BillingChangeResult ApplyProviderStatus(SubscriptionStatus status, DateTime observedAtUtc, DateTime nowUtc)
    {
        if (!SubscriptionStatusTransitions.IsBillingState(status))
            return BillingChangeResult.InvalidTransition;

        if (observedAtUtc < StatusObservedAtUtc)
            return BillingChangeResult.Stale;

        if (status == Status)
        {
            if (observedAtUtc == StatusObservedAtUtc)
                return BillingChangeResult.Unchanged;

            StatusObservedAtUtc = observedAtUtc;
            Touch(nowUtc);
            return BillingChangeResult.Applied;
        }

        if (observedAtUtc == StatusObservedAtUtc)
            return BillingChangeResult.Stale;

        if (!SubscriptionStatusTransitions.IsAllowed(Status, status))
            return BillingChangeResult.InvalidTransition;

        if (SubscriptionStatusTransitions.RequiresProviderReference(status) && ExternalSubscriptionReference is null)
            return BillingChangeResult.InvalidTransition;

        Status = status;
        StatusObservedAtUtc = observedAtUtc;
        Touch(nowUtc);
        return BillingChangeResult.Applied;
    }

    /// <summary>
    /// Records the tenant the owning registration was provisioned into. Set once; another tenant is a
    /// <see cref="BillingChangeResult.Conflict"/>. The caller checks that the registration names this tenant.
    /// </summary>
    public BillingChangeResult LinkTenant(Guid tenantId, DateTime nowUtc)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant id is required.", nameof(tenantId));

        if (TenantId == tenantId)
            return BillingChangeResult.Unchanged;
        if (TenantId is not null)
            return BillingChangeResult.Conflict;

        TenantId = tenantId;
        Touch(nowUtc);
        return BillingChangeResult.Applied;
    }

    /// <summary>
    /// Creates the record of one verified, paid period and moves <see cref="CurrentPeriodEndUtc"/> forward when the
    /// period ends later than the stored one (an older period recorded late never moves it back). The charged price
    /// must equal this subscription's snapshot, and the subscription must already have a provider reference;
    /// otherwise nothing is recorded. Duplicate references are checked by the caller and by unique indexes.
    /// </summary>
    public (BillingChangeResult Result, BillingPayment? Payment) RecordVerifiedPayment(
        string externalPaymentReference,
        BillingPrice price,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        DateTime verifiedAtUtc,
        DateTime nowUtc)
    {
        ValidateReference(externalPaymentReference, nameof(externalPaymentReference));
        if (periodEndUtc <= periodStartUtc)
            throw new ArgumentException("A paid period ends after it starts.", nameof(periodEndUtc));

        // A late report of an earlier period is still evidence of a real charge, so any state may record it; only a
        // subscription the provider never confirmed cannot have a verified payment.
        if (ExternalSubscriptionReference is null)
            return (BillingChangeResult.InvalidTransition, null);

        if (price != Price)
            return (BillingChangeResult.Conflict, null);

        var payment = new BillingPayment(
            this,
            externalPaymentReference,
            price,
            periodStartUtc,
            periodEndUtc,
            verifiedAtUtc,
            nowUtc);

        if (CurrentPeriodEndUtc is null || periodEndUtc > CurrentPeriodEndUtc)
        {
            CurrentPeriodStartUtc = periodStartUtc;
            CurrentPeriodEndUtc = periodEndUtc;
        }

        Touch(nowUtc);
        return (BillingChangeResult.Applied, payment);
    }

    internal static void ValidateReference(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ReferenceMaxLength || value != value.Trim())
            throw new ArgumentException(
                $"A provider reference is 1 to {ReferenceMaxLength} characters without surrounding spaces.",
                parameterName);
    }

    private void Touch(DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        Revision++;
    }
}
