using System.Security.Claims;
using Wasla.Application.Security;

namespace Wasla.UnitTests.Auth;

/// <summary>
/// Tenant session claim sets that differ from what the login issues (<see cref="TenantSessionClaims.Create"/>) by being
/// ambiguous or non-canonical rather than missing: a repeated security claim (identical or not), role claim types that
/// disagree, and role or GUID text the issuer never writes. Each must be rejected before any database is opened.
/// </summary>
internal static class AmbiguousSessionClaims
{
    public static TheoryData<string> Defects =>
    [
        "duplicate-tenant-id-other", "duplicate-tenant-id-identical", "duplicate-user-id-identical",
        "duplicate-legacy-user-id-identical", "duplicate-stamp-other", "duplicate-stamp-identical",
        "duplicate-role-identical", "role-claim-types-disagree", "lowercase-role", "padded-role", "numeric-role",
        "comma-combined-role", "obsolete-staff-role", "uppercase-tenant-id", "braced-stamp"
    ];

    /// <summary>Applies <paramref name="defect"/> to a copy of a valid session's claims.</summary>
    public static List<Claim> Apply(IEnumerable<Claim> validSession, string defect, Guid otherTenantId)
    {
        var claims = validSession.ToList();
        string Value(string type) => claims.Single(c => c.Type == type).Value;
        void Add(string type, string value) => claims.Add(new Claim(type, value));
        void Replace(string type, string value)
        {
            claims.RemoveAll(c => c.Type == type);
            claims.Add(new Claim(type, value));
        }
        void Role(string value)
        {
            Replace(ClaimTypes.Role, value);
            Replace("Role", value);
        }

        const string stamp = WaslaAuthContracts.TenantSecurityStampClaim;
        switch (defect)
        {
            case "duplicate-tenant-id-other": Add(WaslaAuthContracts.TenantIdClaim, otherTenantId.ToString()); break;
            case "duplicate-tenant-id-identical": Add(WaslaAuthContracts.TenantIdClaim, Value(WaslaAuthContracts.TenantIdClaim)); break;
            case "duplicate-user-id-identical": Add(ClaimTypes.NameIdentifier, Value(ClaimTypes.NameIdentifier)); break;
            case "duplicate-legacy-user-id-identical": Add("UserId", Value("UserId")); break;
            case "duplicate-stamp-other": Add(stamp, Guid.NewGuid().ToString()); break;
            case "duplicate-stamp-identical": Add(stamp, Value(stamp)); break;
            case "duplicate-role-identical": Add(ClaimTypes.Role, Value(ClaimTypes.Role)); break;
            // The standard claim says Owner, the legacy one a lower role.
            case "role-claim-types-disagree": Replace("Role", nameof(Wasla.Domain.Enums.UserRole.Viewer)); break;
            case "lowercase-role": Role(Value(ClaimTypes.Role).ToLowerInvariant()); break;
            case "padded-role": Role(" " + Value(ClaimTypes.Role) + " "); break;
            // "1" is Owner's enum value; "Owner, Manager" used to parse as Owner|Manager = Kitchen.
            case "numeric-role": Role("1"); break;
            case "comma-combined-role": Role("Owner, Manager"); break;
            case "obsolete-staff-role": Role("Staff"); break;
            case "uppercase-tenant-id": Replace(WaslaAuthContracts.TenantIdClaim, Value(WaslaAuthContracts.TenantIdClaim).ToUpperInvariant()); break;
            case "braced-stamp": Replace(stamp, "{" + Value(stamp) + "}"); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, null);
        }

        return claims;
    }
}
