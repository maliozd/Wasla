using System.Security.Claims;
using Wasla.Application.Abstractions.Auth;
using Wasla.Application.Security;
using Wasla.Domain.Enums;

namespace Wasla.Web.Security;

/// <summary>
/// The claims a tenant session is validated by. The role is read the way <see cref="TenantRoleAuthorizationHandler"/>
/// reads it, so the role that is validated is the role that is authorized.
/// </summary>
public sealed record TenantSessionClaims(Guid TenantId, Guid UserId, UserRole Role, Guid SecurityStamp)
{
    public TenantUserActor ToActor() => new(UserId, SecurityStamp);

    public static IEnumerable<Claim> Create(Guid tenantId, Guid userId, UserRole role, Guid securityStamp) =>
    [
        new(WaslaAuthContracts.TenantIdClaim, tenantId.ToString()),
        new("UserId", userId.ToString()),
        new("Role", role.ToString()),
        new(ClaimTypes.NameIdentifier, userId.ToString()),
        new(ClaimTypes.Role, role.ToString()),
        new(WaslaAuthContracts.TenantSecurityStampClaim, securityStamp.ToString())
    ];

    /// <summary>
    /// False when a claim is missing, malformed or empty, or when the user id claims disagree.
    /// </summary>
    public static bool TryRead(ClaimsPrincipal? principal, out TenantSessionClaims session)
    {
        session = null!;
        if (principal is null)
            return false;

        if (!TryReadGuid(principal, WaslaAuthContracts.TenantIdClaim, out var tenantId)
            || !TryReadGuid(principal, ClaimTypes.NameIdentifier, out var userId)
            || !TryReadGuid(principal, WaslaAuthContracts.TenantSecurityStampClaim, out var securityStamp))
        {
            return false;
        }

        var legacyUserId = principal.FindFirstValue("UserId");
        if (legacyUserId is not null && (!Guid.TryParse(legacyUserId, out var parsedLegacyUserId) || parsedLegacyUserId != userId))
            return false;

        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? principal.FindFirstValue("Role");
        if (!Enum.TryParse<UserRole>(roleValue, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            return false;

        session = new TenantSessionClaims(tenantId, userId, role, securityStamp);
        return true;
    }

    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value) =>
        Guid.TryParse(principal.FindFirstValue(claimType), out value) && value != Guid.Empty;
}
