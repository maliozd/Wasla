using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.TenantDb;

/// <summary>
/// One employee's choice for routine order actions, within this tenant's database. A missing row means the default:
/// Approve and Mark ready ask for confirmation first. Reject, reprint and device security actions are not covered and
/// always ask. It changes no lifecycle rule, permission or automation.
/// </summary>
public sealed class UserOrderActionPreference : BaseEntity
{
    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public bool ConfirmRoutineActions { get; set; } = true;
}
