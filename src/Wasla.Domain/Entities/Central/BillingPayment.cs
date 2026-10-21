using Wasla.Domain.Billing;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// One provider-verified, paid period of a <see cref="BillingSubscription"/> (WAS-147). Immutable once stored. The
/// owner is the subscription; provider and environment are copied from it so the provider's payment reference is
/// unique within one provider environment. Accounting fields, refunds and invoices are WAS-153.
/// </summary>
public sealed class BillingPayment
{
    private BillingPayment()
    {
    }

    internal BillingPayment(
        BillingSubscription subscription,
        string externalPaymentReference,
        BillingPrice price,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        DateTime verifiedAtUtc,
        DateTime nowUtc)
    {
        Id = Guid.NewGuid();
        SubscriptionId = subscription.Id;
        Provider = subscription.Provider;
        Environment = subscription.Environment;
        ExternalPaymentReference = externalPaymentReference;
        Amount = price.Amount;
        Currency = price.Currency;
        PeriodStartUtc = periodStartUtc;
        PeriodEndUtc = periodEndUtc;
        VerifiedAtUtc = verifiedAtUtc;
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public BillingProvider Provider { get; private set; }

    public BillingEnvironment Environment { get; private set; }

    public string ExternalPaymentReference { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTime PeriodStartUtc { get; private set; }

    public DateTime PeriodEndUtc { get; private set; }

    public DateTime VerifiedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public BillingPrice Price => new(Amount, Currency);

    /// <summary>True when a repeated report describes exactly this payment, so recording it again is a no-op.</summary>
    public bool Describes(string externalPaymentReference, BillingPrice price, DateTime periodStartUtc, DateTime periodEndUtc) =>
        string.Equals(ExternalPaymentReference, externalPaymentReference, StringComparison.Ordinal)
        && Price == price
        && PeriodStartUtc == periodStartUtc
        && PeriodEndUtc == periodEndUtc;
}
