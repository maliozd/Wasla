using Wasla.Application.Security;

namespace Wasla.Web.Security;

public static class AuthSchemes
{
    public const string Tenant = WaslaAuthContracts.TenantScheme;
    public const string CentralAdmin = WaslaAuthContracts.CentralAdminScheme;
}
