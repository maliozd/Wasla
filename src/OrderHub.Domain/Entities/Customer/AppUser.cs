using OrderHub.Domain.Common;
using OrderHub.Domain.Enums;

namespace OrderHub.Domain.Entities.Customer;

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

    public UserRole Role { get; set; } = UserRole.Staff;

    /// <summary>
    /// Optional: restrict a Staff/Manager to a specific branch.
    /// Null means access to all branches.
    /// </summary>
    public Guid? BranchId { get; set; }

    public bool IsActive { get; set; } = true;
}
