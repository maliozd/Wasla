namespace Wasla.Domain.Enums;

/// <summary>
/// The Wasla product a tenant (one restaurant) is assigned to (WAS-235). A restaurant uses Wasla Orders or Wasla POS,
/// never both at once. Wasla Orders processes a tenant only while it is <see cref="Orders"/>. In the two transition
/// states neither product processes it: the cutover (WAS-240) uses them to stop one product before the other starts.
/// The values are stored in CentralDb; never renumber them.
/// </summary>
public enum TenantProduct
{
    /// <summary>The tenant uses Wasla Orders. The database default, so every existing tenant and older build is Orders.</summary>
    Orders = 1,

    /// <summary>Moving from Orders to POS: Orders has stopped starting work for the tenant, POS has not started.</summary>
    MovingToPos = 2,

    /// <summary>The tenant uses Wasla POS. Orders refuses it like an inactive tenant.</summary>
    Pos = 3,

    /// <summary>Moving from POS back to Orders: POS has stopped, Orders has not started.</summary>
    MovingToOrders = 4
}
