using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Central;

public sealed class CentralAdminUser : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Replaced whenever the admin's password changes. A Central Admin session carries the value it was issued with and
    /// is rejected once the two differ. A write outside the supported commands that changes the password or the active
    /// state must replace it in the same statement. Never log or return this value.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTime? LastLoginAt { get; set; }
}

