using Wasla.Domain.Billing;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// Wasla's authoritative price for one plan and billing period from <see cref="EffectiveFromUtc"/> on (WAS-147).
/// CentralDb owns it; a checkout charges the row in effect when it starts, and the subscription keeps that amount as
/// its snapshot. Central Admins add them on the plan prices page; nothing seeds them. A price change is a new row with
/// a later <see cref="EffectiveFromUtc"/>. A row that nothing references yet may be corrected in place (GitHub #64): only
/// with one conditional statement that bumps <see cref="Revision"/> and is recorded as a
/// <see cref="BillingPlanPriceCorrection"/>; a referenced row never changes. EF refuses tracked changes to every column.
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

    /// <summary>The amount the provider charges for one period, in TRY with two decimals.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="Amount"/> already includes VAT, as the Central Admin stated when setting the price. The amount
    /// charged is <see cref="Amount"/> either way; tax handling of the payment is WAS-153.
    /// </summary>
    public bool VatIncluded { get; private set; }

    public DateTime EffectiveFromUtc { get; private set; }

    /// <summary>The Central Admin who set this price; kept for audit, the admin row is not referenced by a key.</summary>
    public Guid CreatedByCentralAdminUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Raised by every correction. A correction names the revision it was based on, and a writer that starts referencing
    /// the row first locks it at the revision it read, so neither can act on values that changed in between.
    /// </summary>
    public int Revision { get; private set; }

    public BillingPrice Price => new(Amount, Currency);

    public static BillingPlanPrice Create(
        string planCode,
        string billingPeriod,
        BillingPrice price,
        bool vatIncluded,
        DateTime effectiveFromUtc,
        Guid createdByCentralAdminUserId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(planCode) || planCode.Length > BillingSubscription.PlanCodeMaxLength || planCode != planCode.Trim())
            throw new ArgumentException("A plan code is required.", nameof(planCode));
        if (billingPeriod is not (BillingSubscription.MonthlyPeriod or BillingSubscription.YearlyPeriod))
            throw new ArgumentException("The billing period is Monthly or Yearly.", nameof(billingPeriod));
        if (price == default)
            throw new ArgumentException("A price is required.", nameof(price));
        if (createdByCentralAdminUserId == Guid.Empty)
            throw new ArgumentException("The Central Admin who sets the price is required.", nameof(createdByCentralAdminUserId));

        return new BillingPlanPrice
        {
            Id = Guid.NewGuid(),
            PlanCode = planCode,
            BillingPeriod = billingPeriod,
            Amount = price.Amount,
            Currency = price.Currency,
            VatIncluded = vatIncluded,
            EffectiveFromUtc = effectiveFromUtc,
            CreatedByCentralAdminUserId = createdByCentralAdminUserId,
            CreatedAtUtc = nowUtc,
            Revision = 1
        };
    }
}
