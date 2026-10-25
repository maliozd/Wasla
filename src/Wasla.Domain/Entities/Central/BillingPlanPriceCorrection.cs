using Wasla.Domain.Billing;

namespace Wasla.Domain.Entities.Central;

/// <summary>
/// The audit record of one in-place correction of a <see cref="BillingPlanPrice"/> that nothing referenced (GitHub #64):
/// which Central Admin changed it, when, and its values before and after. Written in the same transaction as the
/// correction and never changed or deleted. A correction overwrites the price row, so this is the only place its
/// earlier values survive.
/// </summary>
/// <remarks>
/// <see cref="PlanPriceId"/> and <see cref="CorrectedByCentralAdminUserId"/> are kept without a foreign key, like
/// <see cref="BillingPlanPrice.CreatedByCentralAdminUserId"/>: the record outlives a scheduled price that is withdrawn
/// later. An audit record is not a reference that makes the price immutable.
/// </remarks>
public sealed class BillingPlanPriceCorrection
{
    private BillingPlanPriceCorrection()
    {
    }

    public Guid Id { get; private set; }

    public Guid PlanPriceId { get; private set; }

    /// <summary>The price row's <see cref="BillingPlanPrice.Revision"/> after this correction.</summary>
    public int Revision { get; private set; }

    public Guid CorrectedByCentralAdminUserId { get; private set; }

    public DateTime CorrectedAtUtc { get; private set; }

    public decimal PreviousAmount { get; private set; }

    public string PreviousCurrency { get; private set; } = string.Empty;

    public bool PreviousVatIncluded { get; private set; }

    public DateTime PreviousEffectiveFromUtc { get; private set; }

    public decimal NewAmount { get; private set; }

    public string NewCurrency { get; private set; } = string.Empty;

    public bool NewVatIncluded { get; private set; }

    public DateTime NewEffectiveFromUtc { get; private set; }

    public BillingPrice PreviousPrice => new(PreviousAmount, PreviousCurrency);

    public BillingPrice NewPrice => new(NewAmount, NewCurrency);

    public static BillingPlanPriceCorrection Record(
        BillingPlanPrice before,
        BillingPrice newPrice,
        bool newVatIncluded,
        DateTime newEffectiveFromUtc,
        Guid correctedByCentralAdminUserId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (newPrice == default)
            throw new ArgumentException("A price is required.", nameof(newPrice));
        if (correctedByCentralAdminUserId == Guid.Empty)
            throw new ArgumentException("The Central Admin who corrects the price is required.", nameof(correctedByCentralAdminUserId));

        return new BillingPlanPriceCorrection
        {
            Id = Guid.NewGuid(),
            PlanPriceId = before.Id,
            Revision = before.Revision + 1,
            CorrectedByCentralAdminUserId = correctedByCentralAdminUserId,
            CorrectedAtUtc = nowUtc,
            PreviousAmount = before.Amount,
            PreviousCurrency = before.Currency,
            PreviousVatIncluded = before.VatIncluded,
            PreviousEffectiveFromUtc = before.EffectiveFromUtc,
            NewAmount = newPrice.Amount,
            NewCurrency = newPrice.Currency,
            NewVatIncluded = newVatIncluded,
            NewEffectiveFromUtc = newEffectiveFromUtc
        };
    }
}
