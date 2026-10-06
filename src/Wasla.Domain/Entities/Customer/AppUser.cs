using Wasla.Domain.Common;
using Wasla.Domain.Enums;

namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// Application user, scoped to a single customer database.
/// Authentication runs against whichever CustomerDb the current domain resolves to.
/// </summary>
public class AppUser : BaseEntity
{
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// BCrypt hash. Never log or return this value from any API.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Viewer;

    /// <summary>
    /// Optional: restrict an operational user to a specific branch.
    /// Null means access to all branches.
    /// </summary>
    public Guid? BranchId { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// UTC time of the most recent successful password login. Null means no login has been recorded.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
}
