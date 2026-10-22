using Wasla.Domain.Billing;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Wasla's authoritative price for one plan and billing period from <see cref="EffectiveFromUtc"/> on (WAS-147).
/// CentralDb owns it; a checkout charges the row in effect when it starts, and the subscription keeps that amount as
/// its snapshot. Rows never change: a new price is a new row with a later <see cref="EffectiveFromUtc"/>.
/// </summary>
/// <remarks>
/// This is Wasla's price, not the provider's catalog. The payment provider's product and pricing-plan references for
/// a row are provider configuration per environment (WAS-148) and refer to the row by <see cref="Id"/>.
/// </remarks>
public sealed class BillingPlanPrice
{
    private BillingPlanPrice()
    {
    }

    public Guid Id { get; private set; }

    public string PlanCode { get; private set; } = string.Empty;

    public string BillingPeriod { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public BillingPrice Price => new(Amount, Currency);

    public static BillingPlanPrice Create(
        string planCode,
        string billingPeriod,
        BillingPrice price,
        DateTime effectiveFromUtc,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(planCode) || planCode.Length > BillingSubscription.PlanCodeMaxLength || planCode != planCode.Trim())
            throw new ArgumentException("A plan code is required.", nameof(planCode));
        if (billingPeriod is not (BillingSubscription.MonthlyPeriod or BillingSubscription.YearlyPeriod))
            throw new ArgumentException("The billing period is Monthly or Yearly.", nameof(billingPeriod));
        if (price == default)
            throw new ArgumentException("A price is required.", nameof(price));

        return new BillingPlanPrice
        {
            Id = Guid.NewGuid(),
            PlanCode = planCode,
            BillingPeriod = billingPeriod,
            Amount = price.Amount,
            Currency = price.Currency,
            EffectiveFromUtc = effectiveFromUtc,
            CreatedAtUtc = nowUtc
        };
    }
}
