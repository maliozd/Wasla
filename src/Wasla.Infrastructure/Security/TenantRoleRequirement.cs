using Microsoft.AspNetCore.Authorization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Security;

public sealed class TenantRoleRequirement : IAuthorizationRequirement
{
    public TenantRoleRequirement(params UserRole[] allowedRoles)
    {
        AllowedRoles = allowedRoles.ToHashSet();
    }

    public IReadOnlySet<UserRole> AllowedRoles { get; }
}

/// <summary>
/// Tenant role authorization for Wasla.Web and Wasla.Api: the session must belong to the tenant resolved for this
/// request and carry one of the requirement's roles. The session itself has already been revalidated against the tenant
/// database by the tenant cookie scheme, so the role read here is the user's current role.
/// </summary>
public sealed class TenantRoleAuthorizationHandler : AuthorizationHandler<TenantRoleRequirement>
{
    private readonly ICurrentTenantService _currentTenant;

    public TenantRoleAuthorizationHandler(ICurrentTenantService currentTenant)
    {
        _currentTenant = currentTenant;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantRoleRequirement requirement)
    {
        var tenant = _currentTenant.CurrentTenant;
        if (tenant is not null && TenantSessionClaims.IsInRole(context.User, tenant.Id, requirement.AllowedRoles))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public static class TenantRolePolicyRegistration
{
    /// <summary>
    /// Registers every policy in <see cref="WaslaTenantPolicies.AllowedRoles"/>. With
    /// <paramref name="authenticationScheme"/>, each policy also names the scheme it authenticates with.
    /// </summary>
    public static AuthorizationOptions AddWaslaTenantRolePolicies(
        this AuthorizationOptions options,
        string? authenticationScheme = null)
    {
        foreach (var (name, roles) in WaslaTenantPolicies.AllowedRoles)
            options.AddPolicy(name, BuildPolicy(roles, authenticationScheme));

        return options;
    }

    public static AuthorizationPolicy BuildPolicy(IEnumerable<UserRole> roles, string? authenticationScheme = null)
    {
        var builder = authenticationScheme is null
            ? new AuthorizationPolicyBuilder()
            : new AuthorizationPolicyBuilder(authenticationScheme);
        return builder
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRoleRequirement(roles.ToArray()))
            .Build();
    }
}
