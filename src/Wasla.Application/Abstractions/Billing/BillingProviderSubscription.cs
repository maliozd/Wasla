using Wasla.Domain.Billing;
using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// A subscription as the provider reported it in a server-side read, mapped to Wasla terms.
/// <see cref="ObservedAtUtc"/> is when that read (or the authenticated provider event) happened.
/// </summary>
public sealed record BillingProviderSubscription(
    string ExternalSubscriptionReference,
    string? ExternalSubscriberReference,
    SubscriptionStatus Status,
    DateTime ObservedAtUtc,
    IReadOnlyList<BillingProviderPayment> VerifiedPayments);

/// <summary>One paid period the provider confirmed.</summary>
public sealed record BillingProviderPayment(
    string ExternalPaymentReference,
    BillingPrice Price,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    DateTime VerifiedAtUtc);
