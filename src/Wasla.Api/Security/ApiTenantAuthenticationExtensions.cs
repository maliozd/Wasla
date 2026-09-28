using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Wasla.Application.Abstractions.Tenant;
using Wasla.Application.Security;

namespace Wasla.Api.Security;

public static class ApiTenantAuthenticationExtensions
{
    public static IServiceCollection AddWaslaApiTenantAuthentication(
        this IServiceCollection services,
        CookieSecurePolicy securePolicy)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = WaslaAuthContracts.TenantScheme;
            options.DefaultAuthenticateScheme = WaslaAuthContracts.TenantScheme;
            options.DefaultChallengeScheme = WaslaAuthContracts.TenantScheme;
        })
        .AddCookie(WaslaAuthContracts.TenantScheme, options =>
        {
            options.Cookie.Name = WaslaAuthContracts.TenantCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddScoped<IAuthorizationHandler, ApiTenantClaimAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(WaslaAuthContracts.TenantScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new ApiTenantClaimRequirement())
                .Build();
        });

        return services;
    }
}

public sealed class ApiTenantClaimRequirement : IAuthorizationRequirement;

public sealed class ApiTenantClaimAuthorizationHandler : AuthorizationHandler<ApiTenantClaimRequirement>
{
    private readonly ICurrentTenantService _currentTenant;

    public ApiTenantClaimAuthorizationHandler(ICurrentTenantService currentTenant)
    {
        _currentTenant = currentTenant;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ApiTenantClaimRequirement requirement)
    {
        var tenant = _currentTenant.CurrentTenant;
        var claim = context.User.FindFirstValue(WaslaAuthContracts.TenantIdClaim);
        if (tenant is null || !Guid.TryParse(claim, out var tenantId) || tenantId != tenant.Id)
            return Task.CompletedTask;

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
