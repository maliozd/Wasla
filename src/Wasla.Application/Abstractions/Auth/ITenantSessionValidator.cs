using Wasla.Domain.Enums;

namespace Wasla.Application.Abstractions.Auth;

public enum TenantSessionState
{
    Valid,
    UserNotFound,
    UserInactive,
    RoleChanged,
    StampChanged
}

/// <summary>
/// Checks an issued tenant session against the user's current row in that tenant's database. The tenant id must come
/// from server-side tenant resolution, never from the session. It caches nothing, so a deactivation, deletion, role or
/// password change takes effect on the session's next request.
/// </summary>
public interface ITenantSessionValidator
{
    Task<TenantSessionState> ValidateAsync(
        Guid tenantId,
        Guid userId,
        UserRole role,
        Guid securityStamp,
        CancellationToken ct);
}
