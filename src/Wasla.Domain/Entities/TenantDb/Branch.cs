using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.TenantDb;

/// <summary>
/// A physical branch/location of the restaurant. Lives in the tenant database.
/// </summary>
public class Branch : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
