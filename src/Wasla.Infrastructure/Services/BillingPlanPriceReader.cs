using Microsoft.EntityFrameworkCore;
using Wasla.Application.Abstractions.Billing;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// CentralDb implementation of <see cref="IBillingPlanPriceReader"/> (WAS-147): one query for the rows that have taken
/// effect, then <see cref="BillingPlanPriceQueries.InEffect"/>, the rule checkout uses.
/// </summary>
public sealed class BillingPlanPriceReader : IBillingPlanPriceReader
{
    private readonly CentralDbContext _central;
    private readonly TimeProvider _time;

    public BillingPlanPriceReader(CentralDbContext central, TimeProvider time)
    {
        _central = central;
        _time = time;
    }

    public async Task<IReadOnlyList<BillingPriceQuote>> GetPricesInEffectAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var started = await _central.BillingPlanPrices.AsNoTracking()
            .Where(p => p.EffectiveFromUtc <= now)
            .ToListAsync(ct);

        return BillingPlanPriceQueries.InEffect(started, now)
            .Select(BillingPlanPriceQueries.ToQuote)
            .ToList();
    }
}
