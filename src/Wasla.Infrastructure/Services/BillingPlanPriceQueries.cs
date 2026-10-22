using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The one rule for which plan price applies (WAS-147): for a plan and billing period, the row with the latest
/// <see cref="BillingPlanPrice.EffectiveFromUtc"/> that is not after the moment asked about. Checkout, the checkout
/// summary and the Central Admin plan prices page all use it, so they never disagree.
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
}
