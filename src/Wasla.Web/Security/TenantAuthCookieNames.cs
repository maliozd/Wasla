using Wasla.Application.Security;

namespace Wasla.Web.Security;

public static class TenantAuthCookieNames
{
    public const string Active = WaslaAuthContracts.TenantCookieName;
    public const string LegacyOrderHub = WaslaAuthContracts.LegacyTenantCookieName;
}
