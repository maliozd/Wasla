using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Persistence.Central;

namespace Wasla.Infrastructure.Services;

/// <summary>
/// The one definition of "a plan price is referenced" (GitHub #64), and the lock that keeps it race-safe. A referenced
/// price is historical and never changes; only an unreferenced one may be corrected in place.
/// </summary>
/// <remarks>
/// <para>
/// Today exactly one row refers to a plan price by id: <c>BillingSubscriptions.PlanPriceId</c>, the price a checkout
/// was started from. <c>BillingPayments</c> refer to their subscription, not to a price; <c>PendingRegistrations</c>
/// store only the plan code and billing period; there is no invoice table. <c>BillingPlanPriceCorrections</c> is audit
/// and not a reference.
/// </para>
/// <para>
/// A future table that refers to a price by id (for example invoices, WAS-153) must be added to
/// <see cref="ReferencingPlanPriceIds"/>, and its writer must call <see cref="TryLockUnchangedAsync"/> inside the
/// transaction that inserts the reference, before inserting it. A correction takes the same row lock first, so the two
/// are serialized on the price row: a reference committed first makes the correction refuse, and a correction committed
/// first makes the writer read the corrected price again instead of keeping values that no longer exist.
/// </para>
/// </remarks>
internal static class BillingPlanPriceReferences
{
    /// <summary>Every plan price id that something refers to, once per reference.</summary>
    public static IQueryable<Guid> ReferencingPlanPriceIds(CentralDbContext central) =>
        central.BillingSubscriptions.Select(s => s.PlanPriceId);

    /// <summary>The prices of <paramref name="prices"/> that nothing refers to.</summary>
    public static IQueryable<BillingPlanPrice> Unreferenced(this IQueryable<BillingPlanPrice> prices, CentralDbContext central) =>
        prices.Where(p => !ReferencingPlanPriceIds(central).Contains(p.Id));

    /// <summary>The number of references per referenced price; a price that is absent has none.</summary>
    public static async Task<Dictionary<Guid, int>> CountAsync(CentralDbContext central, CancellationToken ct) =>
        await ReferencingPlanPriceIds(central)
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

    public static Task<int> CountAsync(CentralDbContext central, Guid planPriceId, CancellationToken ct) =>
        ReferencingPlanPriceIds(central).CountAsync(id => id == planPriceId, ct);

    /// <summary>
    /// Locks the price row until the current transaction ends, if it still has <paramref name="revision"/>, with a write
    /// that leaves every value as it is (on SQL Server an exclusive row lock; a correction waits for it and then sees
    /// the reference). False when the row is gone or was corrected since it was read; nothing is locked then. Must run
    /// inside a transaction.
    /// </summary>
    public static async Task<bool> TryLockUnchangedAsync(CentralDbContext central, Guid planPriceId, int revision, CancellationToken ct)
    {
        if (central.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The plan price row lock is only held inside a transaction.");

        return await central.BillingPlanPrices
            .Where(p => p.Id == planPriceId && p.Revision == revision)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Revision, p => p.Revision), ct) == 1;
    }
}
