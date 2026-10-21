using Wasla.Domain.Billing;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// CentralDb persistence of Wasla subscriptions (WAS-147). Every write is idempotent or fails with a deterministic
/// <see cref="BillingStoreOutcome"/>; none of them calls the provider. Provider-driven writes name the subscription by
/// its Wasla id and carry the <see cref="BillingProviderScope"/> that verified the data, which must match the record.
/// </summary>
/// <remarks>
/// Reads for a tenant take the tenant id from the server-side tenant context only, never from a request value; a
/// subscription of another tenant is reported as not found.
/// </remarks>
public interface IBillingSubscriptionStore
{
    /// <summary>
    /// Creates the pending subscription for a registration that awaits payment, snapshotting the registration's plan
    /// and period with <paramref name="price"/>. Repeating it with the same scope and price returns the existing
    /// subscription as <see cref="BillingStoreOutcome.Unchanged"/>; a different scope or price is a
    /// <see cref="BillingStoreOutcome.Conflict"/>.
    /// </summary>
    Task<BillingStartResult> StartForRegistrationAsync(
        Guid pendingRegistrationId,
        BillingProviderScope scope,
        BillingPrice price,
        CancellationToken ct);

    Task<BillingSubscriptionView?> GetForRegistrationAsync(Guid pendingRegistrationId, CancellationToken ct);

    Task<BillingSubscriptionView?> GetForTenantAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Finds the Wasla subscription a provider reference belongs to, within one provider environment.</summary>
    Task<Guid?> FindByProviderReferenceAsync(
        BillingProviderScope scope,
        string externalSubscriptionReference,
        CancellationToken ct);

    Task<BillingStoreOutcome> BindProviderReferencesAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        string externalSubscriptionReference,
        string? externalSubscriberReference,
        CancellationToken ct);

    Task<BillingStoreOutcome> ApplyProviderStatusAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        SubscriptionStatus status,
        DateTime observedAtUtc,
        CancellationToken ct);

    Task<BillingStoreOutcome> RecordVerifiedPaymentAsync(
        Guid subscriptionId,
        BillingProviderScope scope,
        BillingProviderPayment payment,
        CancellationToken ct);

    /// <summary>
    /// Records the tenant the owning registration was provisioned into. Refused unless the registration is
    /// provisioned and names exactly this tenant.
    /// </summary>
    Task<BillingStoreOutcome> LinkTenantAsync(Guid subscriptionId, Guid tenantId, CancellationToken ct);
}

public sealed record BillingStartResult(BillingStoreOutcome Outcome, Guid? SubscriptionId);

public enum BillingStoreOutcome
{
    Applied = 0,
    Unchanged = 1,
    NotFound = 2,

    /// <summary>The registration cannot start a checkout (not awaiting payment, or expired).</summary>
    NotEligible = 3,

    Stale = 4,
    InvalidTransition = 5,

    /// <summary>An immutable value, identifier or uniqueness rule refused the write.</summary>
    Conflict = 6,

    /// <summary>The provider scope of the write is not the record's.</summary>
    ScopeMismatch = 7,

    /// <summary>Concurrent writers kept changing the record; nothing was written by this call.</summary>
    ConcurrencyConflict = 8
}

/// <summary>A subscription as Application and later Web/Admin code may read it. No payment or provider payload.</summary>
public sealed record BillingSubscriptionView(
    Guid Id,
    Guid PendingRegistrationId,
    Guid? TenantId,
    BillingProviderScope Scope,
    string PlanCode,
    string BillingPeriod,
    BillingPrice Price,
    SubscriptionStatus Status,
    DateTime StatusObservedAtUtc,
    string? ExternalSubscriptionReference,
    string? ExternalSubscriberReference,
    DateTime? CurrentPeriodStartUtc,
    DateTime? CurrentPeriodEndUtc)
{
    /// <inheritdoc cref="Wasla.Domain.Entities.Central.BillingSubscription.IsVerifiedActive(SubscriptionStatus, DateTime?, DateTime)"/>
    public bool IsVerifiedActive(DateTime nowUtc) =>
        Wasla.Domain.Entities.Central.BillingSubscription.IsVerifiedActive(Status, CurrentPeriodEndUtc, nowUtc);
}
