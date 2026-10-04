using Microsoft.AspNetCore.Authorization;
using Wasla.Domain.Enums;

namespace Wasla.Web.Security;

internal static class TenantAuthorizationPolicyExtensions
{
    public static AuthorizationOptions AddTenantRolePolicy(
        this AuthorizationOptions options,
        string name,
        params UserRole[] roles)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new TenantRoleRequirement(roles));
        });

        return options;
    }
}
