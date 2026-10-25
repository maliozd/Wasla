using System.Security.Claims;
using Wasla.Application.Abstractions.Auth;
using Wasla.Domain.Enums;

namespace Wasla.Application.Security;

/// <summary>
/// The claims a tenant session is issued with and validated by, shared by Wasla.Web and Wasla.Api. The role is read the
/// same way for session validation and for role authorization, so the role that is validated is the role that is
/// authorized.
/// </summary>
/// <remarks>
/// Reading is strict and mirrors <see cref="Create"/>, the only issuer. Each security claim must carry exactly one value:
/// a repeated tenant id, user id, role or stamp claim is rejected, even when the copies are identical, so no reader can
/// pick a different copy than the one that was validated. Ids and stamps are non-empty GUIDs in the issued <c>D</c> form.
/// The role is matched ordinally against the exact names <see cref="Create"/> writes for the assignable roles (no case
/// folding, whitespace, numbers or comma-combined values), so obsolete <see cref="UserRole.Staff"/> is not a session role.
/// The two role claim types and the two user id claim types, where present, must agree.
/// </remarks>
public sealed record TenantSessionClaims(Guid TenantId, Guid UserId, UserRole Role, Guid SecurityStamp)
{
    private const string LegacyUserIdClaim = "UserId";
    private const string LegacyRoleClaim = "Role";

    /// <summary>The roles a tenant session can carry: the assignable roles, by the exact name <see cref="Create"/> writes.</summary>
    private static readonly UserRole[] SessionRoles =
        [UserRole.Owner, UserRole.Manager, UserRole.Kitchen, UserRole.Cashier, UserRole.Viewer];

    public TenantUserActor ToActor() => new(UserId, SecurityStamp);

    public static IEnumerable<Claim> Create(Guid tenantId, Guid userId, UserRole role, Guid securityStamp) =>
    [
        new(WaslaAuthContracts.TenantIdClaim, tenantId.ToString()),
        new(LegacyUserIdClaim, userId.ToString()),
        new(LegacyRoleClaim, role.ToString()),
        new(ClaimTypes.NameIdentifier, userId.ToString()),
        new(ClaimTypes.Role, role.ToString()),
        new(WaslaAuthContracts.TenantSecurityStampClaim, securityStamp.ToString())
    ];

    /// <summary>
    /// False when a security claim is missing, repeated, malformed or empty, when the role is not one of the issued role
    /// names, or when the user id or role claim types disagree.
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

        // The legacy user id claim is optional, but when present it must be single and name the same user.
        if (!TryReadOptionalSingle(principal, LegacyUserIdClaim, out var legacyUserId))
            return false;
        if (legacyUserId is not null && (!TryParseIssuedGuid(legacyUserId, out var parsedLegacyUserId) || parsedLegacyUserId != userId))
            return false;

        if (!TryReadRole(principal, out var role))
            return false;

        session = new TenantSessionClaims(tenantId, userId, role, securityStamp);
        return true;
    }

    /// <summary>
    /// True when the principal's <c>TenantId</c> claim is the resolved tenant and its role is one of
    /// <paramref name="allowedRoles"/>. Tenant role authorization in Web and Api decides with this, under the same strict
    /// reading as <see cref="TryRead"/>.
    /// </summary>
    public static bool IsInRole(ClaimsPrincipal principal, Guid resolvedTenantId, IReadOnlySet<UserRole> allowedRoles) =>
        TryReadGuid(principal, WaslaAuthContracts.TenantIdClaim, out var tenantId)
        && tenantId == resolvedTenantId
        && TryReadRole(principal, out var role)
        && allowedRoles.Contains(role);

    /// <summary>
    /// The role from <see cref="ClaimTypes.Role"/> and the legacy <c>Role</c> claim: at least one present, each at most
    /// once, equal when both are present, and exactly one of the issued role names.
    /// </summary>
    private static bool TryReadRole(ClaimsPrincipal principal, out UserRole role)
    {
        role = default;
        if (!TryReadOptionalSingle(principal, ClaimTypes.Role, out var standard)
            || !TryReadOptionalSingle(principal, LegacyRoleClaim, out var legacy))
        {
            return false;
        }

        var value = standard ?? legacy;
        if (value is null || (standard is not null && legacy is not null && !string.Equals(standard, legacy, StringComparison.Ordinal)))
            return false;

        foreach (var candidate in SessionRoles)
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                role = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        value = default;
        return TryReadOptionalSingle(principal, claimType, out var text)
               && text is not null
               && TryParseIssuedGuid(text, out value);
    }

    /// <summary>A non-empty GUID exactly as <see cref="Guid.ToString()"/> writes it (lowercase <c>D</c> form, nothing around it).</summary>
    private static bool TryParseIssuedGuid(string text, out Guid value) =>
        Guid.TryParseExact(text, "D", out value)
        && value != Guid.Empty
        && string.Equals(value.ToString("D"), text, StringComparison.Ordinal);

    /// <summary>False when the claim type occurs more than once in the principal; otherwise its value, or null.</summary>
    private static bool TryReadOptionalSingle(ClaimsPrincipal principal, string claimType, out string? value)
    {
        value = null;
        var found = false;
        foreach (var claim in principal.FindAll(claimType))
        {
            if (found)
                return false;
            found = true;
            value = claim.Value;
        }

        return true;
    }
}
