using Wasla.Domain.Billing;

namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// What the provider needs to start a hosted checkout for one Wasla subscription. The plan, period and price are the
/// subscription's stored snapshot, never request values. <see cref="PlanPriceId"/> names the Wasla price row, which the
/// adapter maps to the provider's own product and pricing-plan references for its environment (WAS-148). <see cref="IdempotencyKey"/> is derived from the Wasla
/// subscription so a retried command cannot start a second provider subscription where the provider supports it.
/// </summary>
public sealed record BillingCheckoutRequest(
    Guid SubscriptionId,
    Guid PendingRegistrationId,
    Guid PlanPriceId,
    string PlanCode,
    string BillingPeriod,
    BillingPrice Price,
    BillingSubscriberContact Subscriber,
    Uri CallbackUrl,
    string IdempotencyKey);

/// <summary>
/// Contact details the provider requires for the subscriber, taken from the pending registration. Passed through to
/// the provider only; billing tables do not store them.
/// </summary>
public sealed record BillingSubscriberContact(
    string FullName,
    string Email,
    string? Phone,
    string City,
    string Country,
    string? AddressLine);

public sealed record BillingCardUpdateRequest(
    string ExternalSubscriberReference,
    string ExternalSubscriptionReference,
    Uri CallbackUrl);
