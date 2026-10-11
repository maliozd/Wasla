using System.Security.Claims;

namespace Wasla.Application.Security;

/// <summary>
/// The claims a Central Admin session is issued with and validated by.
/// </summary>
/// <remarks>
/// Reading is strict and mirrors <see cref="Create"/>, the only issuer, in the same way as
/// <see cref="TenantSessionClaims"/>: the admin id, the role and the security stamp must each occur exactly once. Ids and
/// stamps are non-empty GUIDs in the issued <c>D</c> form, and the role is exactly <see cref="Role"/>. A session issued
/// before security stamps existed has no stamp claim and is therefore never read.
/// </remarks>
public sealed record CentralAdminSessionClaims(Guid AdminUserId, Guid SecurityStamp)
{
    public const string Role = "CentralAdmin";

    public static IEnumerable<Claim> Create(Guid adminUserId, string email, string displayName, Guid securityStamp) =>
    [
        new(ClaimTypes.NameIdentifier, adminUserId.ToString()),
        new(ClaimTypes.Email, email),
        new(ClaimTypes.Name, displayName),
        new(ClaimTypes.Role, Role),
        new(WaslaAuthContracts.CentralAdminSecurityStampClaim, securityStamp.ToString())
    ];

    /// <summary>False when the admin id, role or stamp claim is missing, repeated, malformed or empty.</summary>
    public static bool TryRead(ClaimsPrincipal? principal, out CentralAdminSessionClaims session)
    {
        session = null!;
        if (principal is null)
            return false;

        if (!TryReadSingle(principal, ClaimTypes.NameIdentifier, out var adminUserIdText)
            || !TryParseIssuedGuid(adminUserIdText, out var adminUserId)
            || !TryReadSingle(principal, WaslaAuthContracts.CentralAdminSecurityStampClaim, out var stampText)
            || !TryParseIssuedGuid(stampText, out var securityStamp)
            || !TryReadSingle(principal, ClaimTypes.Role, out var role)
            || !string.Equals(role, Role, StringComparison.Ordinal))
        {
            return false;
        }

        session = new CentralAdminSessionClaims(adminUserId, securityStamp);
        return true;
    }

    /// <summary>A non-empty GUID exactly as <see cref="Guid.ToString()"/> writes it (lowercase <c>D</c> form, nothing around it).</summary>
    private static bool TryParseIssuedGuid(string text, out Guid value) =>
        Guid.TryParseExact(text, "D", out value)
        && value != Guid.Empty
        && string.Equals(value.ToString("D"), text, StringComparison.Ordinal);

    /// <summary>True when the claim type occurs exactly once in the principal.</summary>
    private static bool TryReadSingle(ClaimsPrincipal principal, string claimType, out string value)
    {
        value = string.Empty;
        var found = false;
        foreach (var claim in principal.FindAll(claimType))
        {
            if (found)
                return false;
            found = true;
            value = claim.Value;
        }

        return found;
    }
}
