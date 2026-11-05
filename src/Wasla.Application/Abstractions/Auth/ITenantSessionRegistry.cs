namespace Wasla.Application.Abstractions.Auth;

/// <summary>
/// The tenant login sessions stored in each tenant's database (WAS-114). A sign-in creates one before its cookie is
/// issued; logout revokes the one it ends. <see cref="ITenantSessionValidator"/> reads them, and nothing else writes
/// them: validation and sliding renewal never create, extend or reactivate a session. The tenant always comes from
/// server-side tenant resolution and selects the database; the session id comes from the authenticated cookie only.
/// </summary>
public interface ITenantSessionRegistry
{
    /// <summary>Stores a new, unrevoked session for the user and returns its id, a new random value on every call.</summary>
    /// <exception cref="TenantSessionUnavailableException">The tenant database could not be written.</exception>
    Task<Guid> CreateAsync(Guid tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Revokes the user's session <paramref name="sessionId"/>. Idempotent: an already revoked session keeps its first
    /// revocation time, and a session that does not exist for that user is neither created nor changed, nor is any other
    /// session. True when this call revoked it.
    /// </summary>
    /// <exception cref="TenantSessionUnavailableException">The tenant database could not be written.</exception>
    Task<bool> RevokeAsync(Guid tenantId, Guid userId, Guid sessionId, CancellationToken ct);
}
