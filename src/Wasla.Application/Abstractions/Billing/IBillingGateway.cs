namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// The payment provider as Wasla needs it (WAS-147): start a hosted checkout, read a verified subscription, and the
/// two management actions of the approved roadmap, cancel and hosted card update. The single implementation is the
/// iyzico adapter in Infrastructure (WAS-148); it maps provider requests and responses to these types and keeps every
/// provider DTO, status string and credential on its side.
/// </summary>
/// <remarks>
/// Card data never passes through this interface: checkout and card update are provider-hosted pages. Every result
/// that reports a subscription is read server-side from the provider, never taken from a browser redirect. A state the
/// adapter cannot map is reported as <see cref="Wasla.Domain.Enums.SubscriptionStatus.ReconciliationRequired"/>.
/// </remarks>
public interface IBillingGateway
{
    /// <summary>The provider account this gateway is configured for; stamped on every record it verifies.</summary>
    BillingProviderScope Scope { get; }

    Task<BillingHostedSession> StartCheckoutAsync(BillingCheckoutRequest request, CancellationToken ct);

    /// <summary>
    /// Reads the outcome of a hosted checkout from the provider. Null when the provider knows no subscription for the
    /// session (abandoned, failed or expired checkout).
    /// </summary>
    Task<BillingProviderSubscription?> GetCheckoutResultAsync(string sessionToken, CancellationToken ct);

    /// <summary>Reads a subscription's current state for reconciliation. Null when the provider does not know it.</summary>
    Task<BillingProviderSubscription?> GetSubscriptionAsync(string externalSubscriptionReference, CancellationToken ct);

    /// <summary>Asks the provider to cancel and returns the state the provider reports afterwards.</summary>
    Task<BillingProviderSubscription> CancelSubscriptionAsync(string externalSubscriptionReference, CancellationToken ct);

    Task<BillingHostedSession> StartCardUpdateAsync(BillingCardUpdateRequest request, CancellationToken ct);
}
