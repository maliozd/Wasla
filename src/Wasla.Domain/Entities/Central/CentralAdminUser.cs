using Wasla.Domain.Common;

namespace Wasla.Domain.Entities.Central;

public sealed class CentralAdminUser : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Issued into every Central Admin session cookie and compared on each request. A new value revokes every
    /// session issued before it. <c>CentralDbContext.SaveChanges</c> replaces it whenever <see cref="PasswordHash"/> or
    /// <see cref="IsActive"/> changes; bulk updates and SQL that change either must set it themselves.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();
}

