namespace Wasla.Application.Abstractions.Auth;

public interface IAuthValidationService
{
    Task<AuthSessionResult?> ValidateAsync(Guid tenantId, string email, string password, CancellationToken ct);

    /// <summary>
    /// The current session data of an active user, read from that tenant's database, for a sign-in that does not
    /// check a password (the signup welcome link). Null when the user does not exist or is inactive.
    /// </summary>
    Task<AuthSessionResult?> GetActiveSessionAsync(Guid tenantId, Guid userId, CancellationToken ct);
}
