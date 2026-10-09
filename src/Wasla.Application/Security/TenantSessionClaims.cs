using System.Security.Claims;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Enums;

namespace Wasla.Application.Security;

/// <summary>
/// The claims a tenant session is issued with and validated by, shared by Wasla.Web and Wasla.Api. The role is read the
/// same way for session validation and for role authorization, so the role that is validated is the role that is
/// authorized.
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

        var legacyUserId = principal.FindFirst("UserId")?.Value;
        if (legacyUserId is not null && (!Guid.TryParse(legacyUserId, out var parsedLegacyUserId) || parsedLegacyUserId != userId))
            return false;

        if (!TryReadRole(principal, out var role))
            return false;

        session = new TenantSessionClaims(tenantId, userId, role, securityStamp);
        return true;
    }

    /// <summary>
    /// True when the principal's <c>TenantId</c> claim is the resolved tenant and its role is one of
    /// <paramref name="allowedRoles"/>. Tenant role authorization in Web and Api decides with this.
    /// </summary>
    public static bool IsInRole(ClaimsPrincipal principal, Guid resolvedTenantId, IReadOnlySet<UserRole> allowedRoles) =>
        TryReadGuid(principal, WaslaAuthContracts.TenantIdClaim, out var tenantId)
        && tenantId == resolvedTenantId
        && TryReadRole(principal, out var role)
        && allowedRoles.Contains(role);

    private static bool TryReadRole(ClaimsPrincipal principal, out UserRole role)
    {
        var roleValue = principal.FindFirst(ClaimTypes.Role)?.Value ?? principal.FindFirst("Role")?.Value;
        return Enum.TryParse(roleValue, ignoreCase: true, out role) && Enum.IsDefined(role);
    }

    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value) =>
        Guid.TryParse(principal.FindFirst(claimType)?.Value, out value) && value != Guid.Empty;
}
