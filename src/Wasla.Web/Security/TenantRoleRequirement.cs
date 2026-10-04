using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Domain.Enums;

namespace Wasla.Web.Security;

public sealed class TenantRoleRequirement : IAuthorizationRequirement
{
    public TenantRoleRequirement(params UserRole[] allowedRoles)
    {
        AllowedRoles = allowedRoles.ToHashSet();
    }

    public IReadOnlySet<UserRole> AllowedRoles { get; }
}

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
        if (tenant is null)
            return Task.CompletedTask;

        var claimTenantId = context.User.FindFirstValue("TenantId");
        if (!Guid.TryParse(claimTenantId, out var tenantId) || tenantId != tenant.Id)
            return Task.CompletedTask;

        var roleValue = context.User.FindFirstValue(ClaimTypes.Role)
            ?? context.User.FindFirstValue("Role");
        if (!Enum.TryParse<UserRole>(roleValue, ignoreCase: true, out var role))
            return Task.CompletedTask;

        if (requirement.AllowedRoles.Contains(role))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
