using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// One user's completion of one product-tour version.
/// The tour key includes its version, for example <c>live-screen-intro:v1</c>.
/// This is not operational readiness and is not shared across users.
/// </summary>
public sealed class UserProductTourCompletion : BaseEntity
{
    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public string TourKey { get; set; } = string.Empty;

    public DateTime CompletedAtUtc { get; set; }
}
