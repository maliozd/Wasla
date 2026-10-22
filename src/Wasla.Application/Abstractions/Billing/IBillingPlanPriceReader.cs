using Wasla.Domain.Billing;

namespace Wasla.Application.Abstractions.Billing;

/// <summary>
/// Read side of the Central Admin plan prices for customer-facing pages (WAS-147): the public pricing section and
/// signup show exactly the prices checkout would charge now. Monthly and yearly are separate prices; one is never
/// derived from the other.
/// </summary>
public interface IBillingPlanPriceReader
{
    /// <summary>
    /// The price in effect now for every plan and billing period that has one. A plan and period without one is absent,
    /// and pages show that its price is not set; there is no built-in fallback amount.
    /// </summary>
    Task<IReadOnlyList<BillingPriceQuote>> GetPricesInEffectAsync(CancellationToken ct);
}

/// <summary>
/// A Wasla plan price as it applies to someone: the <c>BillingPlanPrices</c> row it comes from, the plan and billing
/// period, the amount and currency, and whether VAT is included. For an existing subscription it is the subscription's
/// snapshot, with VAT from the row the subscription was priced from.
/// </summary>
public sealed record BillingPriceQuote(
    Guid PlanPriceId,
    string PlanCode,
    string BillingPeriod,
    BillingPrice Price,
    bool VatIncluded);
