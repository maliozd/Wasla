using Microsoft.EntityFrameworkCore;
using Wasla.Domain.Entities.Customer;

namespace Wasla.Infrastructure.Persistence.Tenant;

/// <summary>
/// The one order in which an order's items, and each item's options, are shown and printed: the provider's position
/// (<see cref="OrderItem.Sequence"/>, <see cref="OrderItemOption.Sequence"/>), then the stored time, then the Id. Rows
/// stored before positions were kept all have position 0 and fall back to the rest: rows that sync inserted were created
/// in the provider's order, while rows that a sync update rewrote share one time and so follow the Id. The database
/// sorts in every case, so all readers agree on ties. Projections cannot call this from inside their expression trees
/// and repeat the same three keys (<c>OrderReadService</c>).
/// </summary>
internal static class OrderItemOrdering
{
    public static IQueryable<Order> IncludeItemsInSequence(this IQueryable<Order> orders) =>
        orders
            .Include(o => o.Items.OrderBy(i => i.Sequence).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id))
            .ThenInclude(i => i.Options.OrderBy(o => o.Sequence).ThenBy(o => o.CreatedAt).ThenBy(o => o.Id));
}
