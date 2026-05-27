using OrderHub.Domain.Common;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// Per-tenant operational settings stored in the CustomerDb.
/// When the row does not exist, defaults are treated as enabled to preserve behavior.
/// </summary>
public sealed class CustomerOperationalSettings : BaseEntity
{
    public bool OrderSyncEnabled { get; set; } = true;
}

