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
    /// Pre-Wasla tenant cookie. It is expired when seen and is not an authentication scheme.
    /// </summary>
    public const string LegacyTenantCookieName = "orderhub_auth";

    public const string TenantIdClaim = "TenantId";
}
