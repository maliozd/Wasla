namespace Wasla.Application.Security;

/// <summary>
/// Canonical cookie-authentication names shared by Wasla.Web and Wasla.Api.
/// </summary>
public static class WaslaAuthContracts
{
    public const string TenantScheme = "WaslaTenant";
    public const string CentralAdminScheme = "WaslaCentralAdmin";
    public const string TenantCookieName = ".Wasla.TenantAuth";
    public const string CentralAdminCookieName = ".Wasla.CentralAdminAuth";

    /// <summary>
    /// The tenant user's security stamp at sign-in. A tenant session whose stamp no longer matches the user's row in
    /// the tenant database is rejected.
    /// </summary>
    public const string TenantSecurityStampClaim = "Wasla.TenantSecurityStamp";

    /// <summary>
    /// The id of the tenant login session the cookie was issued for (WAS-114). A tenant session is accepted only while
    /// that session exists for the cookie's user in the tenant database and has not been revoked by logout.
    /// </summary>
    public const string TenantSessionIdClaim = "Wasla.TenantSessionId";

    /// <summary>
    /// The Central Admin's security stamp at sign-in. A Central Admin session whose stamp no longer matches the admin's
    /// row in CentralDb is rejected.
    /// </summary>
    public const string CentralAdminSecurityStampClaim = "Wasla.CentralAdminSecurityStamp";

    public const string TenantIdClaim = "TenantId";
}
