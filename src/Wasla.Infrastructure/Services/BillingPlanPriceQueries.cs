using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The one rule for which plan price applies (WAS-147): for a plan and billing period, the row with the latest
/// <see cref="BillingPlanPrice.EffectiveFromUtc"/> that is not after the moment asked about. Checkout, the checkout
/// summary, the public pricing and signup pages and the Central Admin plan prices page all use it, so they never
/// disagree. Once a registration has a subscription, its snapshot applies instead (<see cref="ForRegistrationAsync"/>).
/// </summary>
internal static class BillingPlanPriceQueries
{
    public static Task<BillingPlanPrice?> InEffectAsync(
        this IQueryable<BillingPlanPrice> prices,
        string planCode,
        string billingPeriod,
        DateTime nowUtc,
        CancellationToken ct) =>
        prices
            .Where(p => p.PlanCode == planCode && p.BillingPeriod == billingPeriod && p.EffectiveFromUtc <= nowUtc)
            .OrderByDescending(p => p.EffectiveFromUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>The same rule over rows already read: the price in effect for each plan and period that has one.</summary>
    public static IReadOnlyList<BillingPlanPrice> InEffect(IEnumerable<BillingPlanPrice> prices, DateTime nowUtc) =>
        prices
            .Where(p => p.EffectiveFromUtc <= nowUtc)
            .GroupBy(p => (p.PlanCode, p.BillingPeriod))
            .Select(g => g.MaxBy(p => p.EffectiveFromUtc)!)
            .ToList();

    /// <summary>
    /// The price a checkout for this registration charges, and therefore the one its summary shows. A registration
    /// whose subscription has started keeps that subscription's snapshot, with VAT from the very row it was priced from
    /// (<see cref="BillingSubscription.PlanPriceId"/>), whatever was set later; this is the price
    /// <see cref="BillingSubscriptionStore.StartForRegistrationAsync"/> leaves unchanged on a repeated start. Before
    /// that, the price in effect for the registration's plan and period. Null when neither exists, and then no checkout
    /// can start.
    /// </summary>
    public static async Task<BillingPriceQuote?> ForRegistrationAsync(
        CentralDbContext central,
        Guid pendingRegistrationId,
        string planCode,
        string billingPeriod,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var snapshot = await central.BillingSubscriptions.AsNoTracking()
            .Where(s => s.PendingRegistrationId == pendingRegistrationId)
            .Join(
                central.BillingPlanPrices.AsNoTracking(),
                s => s.PlanPriceId,
                p => p.Id,
                (s, p) => new { s.PlanPriceId, s.PlanCode, s.BillingPeriod, s.Amount, s.Currency, p.VatIncluded })
            .FirstOrDefaultAsync(ct);
        if (snapshot is not null)
        {
            return new BillingPriceQuote(
                snapshot.PlanPriceId,
                snapshot.PlanCode,
                snapshot.BillingPeriod,
                new BillingPrice(snapshot.Amount, snapshot.Currency),
                snapshot.VatIncluded);
        }

        var inEffect = await central.BillingPlanPrices.AsNoTracking().InEffectAsync(planCode, billingPeriod, nowUtc, ct);
        return inEffect is null ? null : ToQuote(inEffect);
    }

    public static BillingPriceQuote ToQuote(BillingPlanPrice price) =>
        new(price.Id, price.PlanCode, price.BillingPeriod, price.Price, price.VatIncluded);
}
