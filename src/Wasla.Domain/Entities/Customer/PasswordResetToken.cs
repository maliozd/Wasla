namespace Wasla.Domain.Entities.Customer;

/// <summary>
/// Single-use password reset token metadata for a tenant user.
/// Stores only a token hash; the raw reset token is never persisted.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? UsedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
