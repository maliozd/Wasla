using OrderHub.Domain.Common;

namespace OrderHub.Domain.Entities.Customer;

/// <summary>
/// A physical branch/location of the restaurant. Lives in CustomerDb.
/// </summary>
public class Branch : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
